"""CrossFill engine: source data  ->  target form.

Workflow
  1. Read the source data (a web page, or a record passed in by the caller).
  2. Open the target form.
  3. Match fields: explicit mapping selectors first, automatic matching for
     the rest.
  4. Fill each field according to its element type (text, select, checkbox,
     radio) and read the value back to verify it.
  5. Leave the form unsubmitted. CrossFill never clicks Submit; a person
     reviews the filled form and submits it.

Field values are never written to the log, only field names and selectors,
so logs are safe to share in bug reports.
"""

from __future__ import annotations

import json
import logging
from dataclasses import asdict, dataclass, field
from typing import Callable

from playwright.sync_api import Browser, Locator, Playwright, sync_playwright
from playwright.sync_api import TimeoutError as PlaywrightTimeout

from .mapping import load_mapping
from .mapping import source_fields as mapping_source_fields
from .matcher import auto_match
from .sources import read_web_source

log = logging.getLogger("crossfill")

TRUTHY = {"true", "yes", "y", "1", "on", "checked", "x"}
_MARK = "data-crossfill-filled"   # set on elements already claimed by a selector

__all__ = ["TransferResult", "FillError", "launch_browser", "fill_element", "fill_target",
           "run_transfer", "load_mapping"]


class FillError(RuntimeError):
    """A value could not be put into a field (e.g. no such option in a select)."""


@dataclass
class TransferResult:
    ok: bool
    target_url: str
    dry_run: bool = False
    data: dict = field(default_factory=dict)          # values read from the source
    filled: dict = field(default_factory=dict)        # field -> selector used (or planned)
    verified: dict = field(default_factory=dict)      # field -> value now in the target
    unmatched: list = field(default_factory=list)     # fields with no target element
    mismatched: list = field(default_factory=list)    # filled, but read-back differs
    skipped: list = field(default_factory=list)       # empty in the source, left alone
    error: str | None = None

    def to_json(self) -> str:
        return json.dumps(asdict(self))

    def summary(self) -> str:
        """Human-readable one-paragraph summary (contains no field values)."""
        state = "PLANNED (dry run)" if self.dry_run else ("OK" if self.ok else "INCOMPLETE")
        lines = [f"CrossFill {state}: {self.target_url}"]
        for key, how in self.filled.items():
            flag = " (MISMATCH)" if key in self.mismatched else ""
            lines.append(f"  {key:<14} -> {how}{flag}")
        if self.unmatched:
            lines.append(f"  unmatched: {', '.join(self.unmatched)}")
        if self.skipped:
            lines.append(f"  skipped (empty): {', '.join(self.skipped)}")
        if self.error:
            lines.append(f"  error: {self.error}")
        return "\n".join(lines)


def launch_browser(p: Playwright, headless: bool) -> Browser:
    """Launch Playwright's Chromium; fall back to installed Chrome or Edge."""
    errors = []
    for kwargs in ({}, {"channel": "chrome"}, {"channel": "msedge"}):
        try:
            return p.chromium.launch(headless=headless, **kwargs)
        except Exception as exc:  # noqa: BLE001 - try the next browser
            errors.append(f"{kwargs or 'bundled chromium'}: {exc.__class__.__name__}")
    raise RuntimeError(
        "No browser could be started. Run 'playwright install chromium'. "
        + "; ".join(errors)
    )


_OPTION_JS = """
(sel, wanted) => {
  const w = wanted.trim().toLowerCase();
  for (const o of sel.options) {
    if (o.value.toLowerCase() === w || o.label.trim().toLowerCase() === w) return o.value;
  }
  return null;
}
"""

_RADIO_JS = """
(el, wanted) => {
  const w = wanted.trim().toLowerCase();
  const label = el.labels && el.labels[0] ? el.labels[0].innerText.trim().toLowerCase() : '';
  return el.value.toLowerCase() === w || label === w;
}
"""


def _describe(loc: Locator) -> tuple[str, str]:
    return loc.evaluate(
        "e => [e.tagName.toLowerCase(), (e.getAttribute('type') || '').toLowerCase()]"
    )


def fill_element(targets: Locator, value: str) -> tuple[str, bool]:
    """Put ``value`` into the element(s) matched by ``targets``.

    Returns ``(value_read_back, matches_expected)``. ``targets`` may match a
    radio group; for every other type the first match is used.
    """
    el = targets.first
    tag, kind = _describe(el)
    el.scroll_into_view_if_needed()

    if tag == "select":
        option = el.evaluate(_OPTION_JS, value)
        if option is None:
            raise FillError(f"no option matching {value!r}")
        el.select_option(value=option)
        got = el.input_value()
        return got, got == option

    if tag == "input" and kind == "checkbox":
        want = value.strip().lower() in TRUTHY
        el.set_checked(want)
        got = el.is_checked()
        return ("true" if got else "false"), got == want

    if tag == "input" and kind == "radio":
        for i in range(targets.count()):
            radio = targets.nth(i)
            if radio.evaluate(_RADIO_JS, value):
                radio.check()
                return radio.input_value(), radio.is_checked()
        raise FillError(f"no radio button matching {value!r}")

    el.fill(value)
    got = el.input_value()
    return got, got == value


def fill_target(page, mapping: dict, data: dict, result: TransferResult,
                *, dry_run: bool = False) -> None:
    """Open the target form, match fields and fill them (or just plan, on dry run)."""
    log.info("Opening target %s", mapping["target_url"])
    page.goto(mapping["target_url"], wait_until="domcontentloaded")
    form = page.locator(mapping["form_selector"]).first
    form.wait_for(state="attached", timeout=15000)

    all_fields = form.locator("input, textarea, select")
    synonyms = mapping.get("synonyms")
    plan: list[tuple[str, Locator, str]] = []

    pending = []
    for key, value in data.items():
        if not value:
            result.skipped.append(key)
        else:
            pending.append(key)

    # Pass 1: explicit selectors claim their elements first, so the
    # auto-matcher can never pick an element a selector already owns.
    remaining = []
    for key in pending:
        selector = mapping["fields"].get(key)
        targets = form.locator(selector) if selector else None
        if targets is not None and targets.count():
            targets.first.evaluate(f"e => e.setAttribute('{_MARK}', '')")
            plan.append((key, targets, selector))
        else:
            if selector:
                log.warning("Selector %r for '%s' not found; trying auto-match", selector, key)
            remaining.append(key)

    # Pass 2: automatic matching for everything else.
    taken = set(form.evaluate(
        f"f => [...f.querySelectorAll('input, textarea, select')]"
        f".flatMap((e, i) => e.hasAttribute('{_MARK}') ? [i] : [])"
    ))
    for key in remaining:
        idx = auto_match(form, key, taken, synonyms)
        if idx is None:
            log.warning("No field found for '%s'", key)
            result.unmatched.append(key)
            continue
        taken.add(idx)
        plan.append((key, all_fields.nth(idx), f"auto-match #{idx}"))

    for key, targets, how in plan:
        result.filled[key] = how
        if dry_run:
            continue
        try:
            got, same = fill_element(targets, data[key])
        except FillError as exc:
            log.warning("Could not fill '%s': %s", key, exc)
            result.mismatched.append(key)
            continue
        result.verified[key] = got
        if not same:
            log.warning("'%s' reads back differently after filling", key)
            result.mismatched.append(key)
        log.info("Filled '%s' via %s", key, how)

    # Remove our bookkeeping attribute so the page is left as we found it.
    form.evaluate(f"f => f.querySelectorAll('[{_MARK}]').forEach(e => e.removeAttribute('{_MARK}'))")


def run_transfer(
    source_url: str | None = None,
    mapping: dict | None = None,
    *,
    data: dict | None = None,
    source_fields: dict | None = None,
    headless: bool = False,
    keep_open: bool = False,
    dry_run: bool = False,
    on_filled: Callable[[TransferResult], None] | None = None,
) -> TransferResult:
    """Copy data into the target form described by ``mapping``. Never submits.

    The data comes from ``data`` if given, otherwise from the web page at
    ``source_url`` (or the mapping's ``source.url``).

    ``on_filled(result)`` is called once the form is filled (before any
    review wait), so a caller such as the web app can report progress while
    the browser stays open for the user.
    """
    mapping = mapping if mapping is not None else load_mapping()
    source_url = source_url or (mapping.get("source") or {}).get("url")
    if data is None and not source_url:
        raise ValueError("Give either data or a source URL (or set source.url in the mapping).")

    result = TransferResult(ok=False, target_url=mapping["target_url"], dry_run=dry_run)
    reported = False

    def report():
        nonlocal reported
        if on_filled and not reported:
            reported = True
            on_filled(result)

    with sync_playwright() as p:
        browser = launch_browser(p, headless)
        try:
            page = browser.new_page()
            page.set_default_timeout(20000)

            if data is not None:
                result.data = {k: str(v).strip() for k, v in data.items()}
            else:
                log.info("Reading source %s", source_url)
                result.data = read_web_source(
                    page, source_url, source_fields or mapping_source_fields(mapping)
                )
            if not any(result.data.values()):
                raise ValueError("The source is empty - nothing to transfer.")

            fill_target(page, mapping, result.data, result, dry_run=dry_run)
            result.ok = not result.unmatched and not result.mismatched

            report()

            if keep_open and not headless:
                # Leave the window up so the user can review and submit by hand.
                page.wait_for_event("close", timeout=0)
        except PlaywrightTimeout as exc:
            if not reported:
                result.ok = False
                result.error = f"Timed out: {exc}"
        except Exception as exc:  # noqa: BLE001 - report every failure to the caller
            # After the result was reported, an error here only means the user
            # closed the review window, which is expected.
            if not reported:
                result.ok = False
                result.error = str(exc)
        finally:
            report()
            try:
                browser.close()
            except Exception:  # noqa: BLE001, S110 - browser may already be closed by the user
                pass
    return result
