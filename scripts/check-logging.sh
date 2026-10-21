#!/usr/bin/env bash
# =============================================================================
# check-logging.sh — logging-convention audit + ratchet gate for the Lingarr
# server-side code (Lingarr.Server / Lingarr.Core / Lingarr.Contracts /
# Lingarr.Migrations).
#
# Modes:
#   scripts/check-logging.sh                    report counts + hits, exit 0
#   scripts/check-logging.sh --gate             ratchet against
#                                               scripts/logging-baseline.txt:
#                                               any count ABOVE baseline ->
#                                               exit 1; below -> hint to
#                                               tighten via --update-baseline
#   scripts/check-logging.sh --update-baseline  rewrite the baseline file with
#                                               the currently measured counts
#
# The script anchors itself to the repository root via its own location, so it
# can be invoked from any working directory (local or CI). It only needs bash,
# find and awk — no .NET SDK — so it runs on macOS and on ubuntu-latest.
#
# Counted buckets (definitions are also printed in report mode):
#   ex_message_template      an interpolated $"..." embedding {<id>.Message}
#                            inside a LOG statement (the exception text is
#                            pre-formatted into the message instead of being
#                            passed structured). Scoped to extracted .Log*
#                            (...) statements: exception wrapping (throw new
#                            / Message = $"..."), DB error columns and
#                            telemetry verdicts legitimately interpolate
#                            ex.Message outside logging and are not counted.
#   double_format            a log statement that passes the exception object
#                            AND manually passes <id>.Message / <id>.StackTrace
#                            as template values, e.g.
#                            LogError(ex, "... {ExceptionMessage}, Stack Trace:
#                            {StackTrace}", ex.Message, ex.StackTrace)
#   template_from_exception  Log(Error|Warning|Critical)(<id>, <id>.Message) —
#                            the exception message itself used as the template
#   color_markers            distinct source lines containing pipe color
#                            markers (|Green|, |Red|, |/Green|, ...);
#                            ColoredConsoleLogger.cs (the renderer that defines
#                            the markers) is excluded
#   apikey_in_logs           a log statement whose template/arguments contain
#                            plaintext "apikey=" / "apiKey="
#
# Ungated advisory: source lines where an "apikey=" / "apiKey=" appears inside
# a string literal — i.e. URL construction such as
#   $"{settings.Url}{apiUrl}apikey={settings.ApiKey}"
# That is request construction, not a log statement (so it is not counted in
# apikey_in_logs), but logging such a URL would leak the key. Identifier
# assignments (ApiKey = ...) are not flagged. Listed for visibility only.
#
# Heuristics: log statements are extracted from ".Log<Level>(" to balanced
# parentheses, capped at 10 lines; unbalanced parentheses inside string
# literals can truncate a statement, so the extractor is best-effort.
# =============================================================================

set -euo pipefail
export LC_ALL=C

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

BASELINE_FILE="${SCRIPT_DIR}/logging-baseline.txt"
GATED_KEYS="ex_message_template double_format template_from_exception color_markers apikey_in_logs"

# ---------------------------------------------------------------------------
# Collect the .cs files to scan (relative to the repository root).
# Prune bin/, obj/ and Tests/ directories; the test projects live outside the
# four scanned roots anyway.
# ---------------------------------------------------------------------------
CS_FILES=()
while IFS= read -r f; do
    CS_FILES+=("$f")
done < <(find Lingarr.Server Lingarr.Core Lingarr.Contracts Lingarr.Migrations \
    \( -name bin -o -name obj -o -name Tests \) -prune -o \
    -type f -name '*.cs' -print)

if [ "${#CS_FILES[@]}" -eq 0 ]; then
    echo "ERROR: no .cs files found under the scan roots — run from the repo root or check the paths." >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# Single awk pass over all files. Emits two record kinds on stdout:
#   COUNT<TAB>bucket<TAB>number
#   HIT<TAB>bucket<TAB>file:line<TAB>excerpt
# ---------------------------------------------------------------------------
AWK_PROG='
BEGIN {
    n_ex = 0; n_df = 0; n_tf = 0; n_cm = 0; n_ak = 0; n_adv = 0
    collect = 0
}
function emit(bucket, f, l, txt,    s) {
    s = txt
    gsub(/[ \t]+/, " ", s)
    if (length(s) > 150) s = substr(s, 1, 150) "..."
    print "HIT\t" bucket "\t" f ":" l "\t" s
}
function classify(f, l, s,    lower) {
    lower = tolower(s)
    # [1] interpolated $"..." embedding {<id>.Message} INSIDE a log
    #     statement. Scoped to extracted .Log*(...) statements: exception
    #     wrapping (throw new / Message = $ "..."), DB error columns and
    #     telemetry verdicts legitimately interpolate ex.Message outside
    #     logging — those are not violations.
    if (s ~ /\$@?"[^"]*\{[A-Za-z_][A-Za-z0-9_]*\.Message\}/ && f !~ /Exception\.cs$/) {
        n_ex++
        emit("ex_message_template", f, l, s)
    }
    # [5] apikey plaintext inside a log statement (independent of the others)
    if (lower ~ /apikey[ \t]*=/) {
        n_ak++
        emit("apikey_in_logs", f, l, s)
    }
    # [3] exception message used AS the message template
    if (s ~ /\.Log(Error|Warning|Critical)\([ \t]*[A-Za-z_][A-Za-z0-9_]*,[ \t]*[A-Za-z_][A-Za-z0-9_]*\.Message[ \t]*\)/) {
        n_tf++
        emit("template_from_exception", f, l, s)
        return
    }
    # [2] exception object passed AND <id>.Message / <id>.StackTrace also
    #     passed manually as template values (double formatting)
    if (s ~ /\.Log(Error|Warning|Critical|Information)\([ \t]*[A-Za-z_][A-Za-z0-9_]*[ \t]*,/ &&
        s ~ /[A-Za-z_][A-Za-z0-9_]*\.(Message|StackTrace)[ \t]*[,)]/) {
        n_df++
        emit("double_format", f, l, s)
        return
    }
}
{
    line = $0

    # ---- per-line rules (run on every line, independent of statements) ----
    # [1] lives in classify(): only extracted log statements are scanned —
    #     per-line matching misfires on exception wrapping, DB error
    #     columns and telemetry verdict strings.
    # [4] pipe color markers; the renderer that defines them is excluded
    if (line ~ /\|\/?(Green|Red|Yellow|Orange|Blue|Cyan|Magenta|Gray|Grey|White|Black|Reset|Bold|Underline|DarkRed|DarkGreen|DarkBlue|DarkYellow|DarkMagenta|DarkCyan)\|/ && \
        FILENAME !~ /ColoredConsoleLogger\.cs$/) {
        n_cm++
        emit("color_markers", FILENAME, FNR, line)
    }
    # advisory: code lines (not comments) where apikey=/apiKey= appears
    # inside a string literal (URL construction) — identifier assignments
    # like "ApiKey = ..." are NOT URL leaks and are not flagged
    if (line ~ /"[^"]*[Aa]pi[Kk]ey[ \t]*=/ && line !~ /^[ \t]*(\/\/|\/\*|\*)/) {
        n_adv++
        emit("apikey_url_advisory", FILENAME, FNR, line)
    }

    # ---- best-effort log-statement extractor ----
    if (collect) {
        depth += gsub(/\(/, "(", line) - gsub(/\)/, ")", line)
        stmt = stmt " " line
        nsl++
        if (depth <= 0 || nsl >= 10) {
            classify(sfile, sline, stmt)
            collect = 0
        }
        next
    }
    if (line ~ /\.Log(Error|Warning|Information|Debug|Critical|Trace)?[ \t]*\(/) {
        collect = 1
        nsl = 0
        stmt = line
        sfile = FILENAME
        sline = FNR
        depth = gsub(/\(/, "(", line) - gsub(/\)/, ")", line)
        if (depth <= 0) {
            classify(sfile, sline, stmt)
            collect = 0
        }
        next
    }
}
END {
    print "COUNT\tex_message_template\t" n_ex
    print "COUNT\tdouble_format\t" n_df
    print "COUNT\ttemplate_from_exception\t" n_tf
    print "COUNT\tcolor_markers\t" n_cm
    print "COUNT\tapikey_in_logs\t" n_ak
    print "COUNT\tapikey_url_advisory\t" n_adv
}
'

RESULTS_FILE="$(mktemp "${TMPDIR:-/tmp}/lingarr-logging.XXXXXX")"
trap 'rm -f "${RESULTS_FILE}"' EXIT

awk "${AWK_PROG}" "${CS_FILES[@]}" > "${RESULTS_FILE}"

count_of() {
    awk -F'\t' -v k="$1" '$1 == "COUNT" && $2 == k { print $3 }' "${RESULTS_FILE}"
}

print_hits() {
    awk -F'\t' -v k="$1" '$1 == "HIT" && $2 == k { print "      - " $3 "  " $4 }' "${RESULTS_FILE}"
}

usage() {
    cat <<'EOF'
Usage: scripts/check-logging.sh [--gate | --update-baseline]

  (no argument)       report the five violation counts with hits (exit 0)
  --gate              fail (exit 1) if any current count EXCEEDS its value in
                      scripts/logging-baseline.txt (ratchet: improvements pass)
  --update-baseline   rewrite scripts/logging-baseline.txt with current counts
EOF
}

mode_report() {
    echo "===================================================================="
    echo " Lingarr logging audit — scripts/check-logging.sh"
    echo "===================================================================="
    echo " scan roots   : Lingarr.Server, Lingarr.Core, Lingarr.Contracts,"
    echo "               Lingarr.Migrations (*.cs; bin/, obj/, Tests/ pruned)"
    echo " files scanned: ${#CS_FILES[@]}"
    echo
    echo "[1] ex_message_template = $(count_of ex_message_template)"
    echo "    rule: an interpolated \$\"...\" embedding {<id>.Message inside"
    echo "          an extracted log statement (.Log*(...)). Exception"
    echo "          wrapping, DB error columns and telemetry verdict strings"
    echo "          outside logging are legitimate and not counted."
    echo "    hits:"
    print_hits ex_message_template
    echo
    echo "[2] double_format = $(count_of double_format)"
    echo "    rule: a log statement that passes the exception object AND"
    echo "          manually passes <id>.Message / <id>.StackTrace as template"
    echo "          values, e.g. LogError(ex, \"... {ExceptionMessage}, Stack"
    echo "          Trace: {StackTrace}\", ex.Message, ex.StackTrace)."
    echo "    hits:"
    print_hits double_format
    echo
    echo "[3] template_from_exception = $(count_of template_from_exception)"
    echo "    rule: Log(Error|Warning|Critical)(<id>, <id>.Message) — the"
    echo "          exception message itself is used as the message template."
    echo "    hits:"
    print_hits template_from_exception
    echo
    echo "[4] color_markers = $(count_of color_markers)"
    echo "    rule: distinct source lines containing pipe color markers"
    echo "          (|Green|, |Red|, |Orange|, |/Green|, ...); opening and"
    echo "          closing tags on the same line count once."
    echo "          ColoredConsoleLogger.cs (the renderer defining the"
    echo "          markers) is excluded."
    echo "    hits:"
    print_hits color_markers
    echo
    echo "[5] apikey_in_logs = $(count_of apikey_in_logs)"
    echo "    rule: a log statement whose template or arguments contain"
    echo "          plaintext \"apikey=\" / \"apiKey=\"."
    echo "    hits:"
    print_hits apikey_in_logs
    echo
    echo "    advisory (NOT gated, NOT part of the five counts):"
    echo "    $(count_of apikey_url_advisory) code lines build URLs with an embedded"
    echo "    apikey= outside of log statements. These construct upstream"
    echo "    requests rather than log messages — kept visible because logging"
    echo "    such a URL would leak the key."
    print_hits apikey_url_advisory
    echo
    echo "exit 0 (report mode always succeeds; use --gate to enforce the baseline)"
}

mode_gate() {
    if [ ! -f "${BASELINE_FILE}" ]; then
        echo "GATE FAIL: baseline file not found: ${BASELINE_FILE}" >&2
        echo "           generate it with: scripts/check-logging.sh --update-baseline" >&2
        exit 1
    fi
    fail=0
    for key in ${GATED_KEYS}; do
        cur="$(count_of "$key")"
        cur="${cur:-0}"
        base="$(awk -F= -v k="${key}" '
            $0 !~ /^[ \t]*#/ && $1 == k { gsub(/[ \t\r]/, "", $2); print $2 }' \
            "${BASELINE_FILE}")"
        if [ -z "${base}" ]; then
            echo "GATE FAIL: baseline file is missing key: ${key}"
            fail=1
            continue
        fi
        if [ "${cur}" -gt "${base}" ]; then
            echo "GATE FAIL: ${key} regressed — current ${cur} > baseline ${base}"
            print_hits "$key"
            fail=1
        elif [ "${cur}" -lt "${base}" ]; then
            echo "ok      : ${key} improved (${cur} < baseline ${base}); consider 'scripts/check-logging.sh --update-baseline' to tighten"
        else
            echo "ok      : ${key} ${cur} == baseline ${base}"
        fi
    done
    if [ "${fail}" -eq 0 ]; then
        echo "Logging gate passed."
    fi
    exit "${fail}"
}

mode_update() {
    {
        echo "# Generated by 'scripts/check-logging.sh --update-baseline' — do not hand-edit."
        echo "# Ratchet baseline: --gate fails when a current count EXCEEDS its baseline value."
        for key in ${GATED_KEYS}; do
            printf '%s=%s\n' "${key}" "$(count_of "${key}")"
        done
    } > "${BASELINE_FILE}"
    echo "Baseline written to ${BASELINE_FILE}:"
    cat "${BASELINE_FILE}"
}

case "${1:---report}" in
    --report | -r | "")
        mode_report
        ;;
    --gate)
        mode_gate
        ;;
    --update-baseline)
        mode_update
        ;;
    -h | --help)
        usage
        ;;
    *)
        echo "unknown option: $1" >&2
        usage
        exit 2
        ;;
esac
