#!/usr/bin/env bash
# =============================================================
# Game.OS – GitHub Mode Checker & Auto-Fixer  (Linux / macOS)
# =============================================================
# Run this script to diagnose AND fix the issue where the
# Game.OS webpage is stuck in demo mode.
#
# What this script does:
#   1. Downloads the deployed script.js from GitHub Pages
#   2. Checks whether DATA_REPO_TOKEN was injected at build time
#   3. XOR-decodes the token (same logic as script.js) and tests
#      GitHub API connectivity
#   4. Reports root-cause diagnosis with plain-text instructions
#   5. If the site is stuck in demo mode, offers to automatically
#      trigger the Deploy workflow, wait for it to finish, and
#      verify the site is back online – no browser needed.
#   6. Opens the relevant GitHub pages in xdg-open (if available)
#
# How to run:
#   chmod +x check-github-mode.sh && ./check-github-mode.sh
#
# Requirements: bash, curl, python3  (all present on Bazzite / any modern Linux)
# =============================================================

set -euo pipefail

REPO_OWNER="Koriebonx98"
REPO_NAME="Game.OS.Userdata"
DATA_REPO="Game.OS.Private.Data"
XOR_KEY="GameOS_KEY"
PAGES_URL="https://Koriebonx98.github.io/Game.OS.Userdata"
SCRIPT_URL="${PAGES_URL}/script.js"

# Workflow polling configuration (mirrors check-github-mode.vbs constants)
WORKFLOW_REGISTRATION_DELAY=5    # seconds to wait after dispatch before first poll
CDN_PROPAGATION_DELAY=8          # seconds to wait after deploy before re-fetching page
POLL_INTERVAL=10                 # seconds between each status poll
MAX_POLL_ATTEMPTS=60             # 60 × 10 s = 10 minutes maximum wait
FEEDBACK_AFTER_ATTEMPTS=6        # show "still waiting" message after this many attempts

# ── Colour helpers ────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m'

_ok()      { echo -e "${GREEN}✅ $*${NC}"; }
_warn()    { echo -e "${YELLOW}⚠  $*${NC}"; }
_error()   { echo -e "${RED}❌ $*${NC}"; }
_heading() { echo -e "\n${BOLD}$*${NC}"; }
_info()    { echo -e "${CYAN}ℹ  $*${NC}"; }

# ── Open a URL in the default browser (best-effort) ──────────
_open_url() {
    local url="$1"
    if command -v xdg-open &>/dev/null; then
        xdg-open "${url}" &>/dev/null &
    elif command -v open &>/dev/null; then   # macOS
        open "${url}" &>/dev/null &
    fi
}

# ── 1. Fetch the deployed script.js ──────────────────────────
_heading "Game.OS GitHub Mode Checker"
echo "Checking: ${PAGES_URL}"
echo ""

SCRIPT_BODY=""
if ! SCRIPT_BODY=$(curl -sf --max-time 20 "${SCRIPT_URL}"); then
    _error "Could not download the deployed script.js."
    echo ""
    echo "  URL : ${SCRIPT_URL}"
    echo ""
    echo "Possible causes:"
    echo "  • GitHub Pages has not been deployed yet"
    echo "  • Pages source is not set to 'GitHub Actions'"
    echo "  • No internet connection"
    echo ""
    echo "Fix: go to  github.com/${REPO_OWNER}/${REPO_NAME}/settings/pages"
    echo "and confirm Source is set to 'GitHub Actions', then trigger the Deploy workflow."
    exit 1
fi

# ── 2. Check whether GITHUB_TOKEN_ENCODED is present ─────────
ENCODED=$(python3 - <<'PYEOF'
import sys, re
data = sys.stdin.read()
m = re.search(r'const GITHUB_TOKEN_ENCODED\s*=\s*"([0-9a-fA-F]+)"', data)
print(m.group(1) if m else '')
PYEOF
<<< "${SCRIPT_BODY}")

TOKEN_SET=false
[ -n "${ENCODED}" ] && TOKEN_SET=true

# ── 3. XOR-decode the token (mirrors script.js runtime logic) ─
TOKEN=""
if [ "${TOKEN_SET}" = "true" ]; then
    TOKEN=$(python3 - "${ENCODED}" "${XOR_KEY}" <<'PYEOF'
import sys
encoded, key = sys.argv[1], sys.argv[2].encode()
n = len(encoded) // 2
sys.stdout.write(''.join(chr(int(encoded[i*2:i*2+2], 16) ^ key[i % len(key)]) for i in range(n)))
PYEOF
)
fi

# ── 4. Test GitHub API connectivity ──────────────────────────
API_OK=false
API_STATUS="not tested (no token injected)"
API_HTTP=0

if [ "${TOKEN_SET}" = "true" ] && [ -n "${TOKEN}" ]; then
    API_URL="https://api.github.com/repos/${REPO_OWNER}/${DATA_REPO}"
    API_HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 15 \
        -H "Authorization: Bearer ${TOKEN}" \
        -H "Accept: application/vnd.github+json" \
        -H "X-GitHub-Api-Version: 2022-11-28" \
        "${API_URL}" 2>/dev/null || echo "0")

    case "${API_HTTP}" in
        200) API_OK=true; API_STATUS="200 OK – repository is accessible" ;;
        401) API_STATUS="401 Unauthorized – token is invalid or expired" ;;
        403) API_STATUS="403 Forbidden – token lacks Contents: Read and write permission" ;;
        404) API_STATUS="404 Not Found – repository does not exist or token has no access to it" ;;
        *)   API_STATUS="HTTP ${API_HTTP}" ;;
    esac
fi

# ── 5. All good? ──────────────────────────────────────────────
if [ "${TOKEN_SET}" = "true" ] && [ "${API_OK}" = "true" ]; then
    _heading "✅  GitHub mode is fully operational."
    echo ""
    echo "  Deployed page : ${PAGES_URL}"
    BYTE_COUNT=$(( ${#ENCODED} / 2 ))
    echo "  Token         : ✅ Injected (${BYTE_COUNT} bytes)"
    echo "  GitHub API    : ✅ ${API_STATUS}"
    echo ""
    echo "If you still cannot log in, try:"
    echo "  • Hard-refresh the page (Ctrl+Shift+R)"
    echo "  • Clear browser site data for the Game.OS page"
    echo "  • Use the Sign Up page to recreate your account"
    exit 0
fi

# ── 6. Determine root cause ───────────────────────────────────
NEEDS_NEW_PAT=false
NEEDS_DISPATCH=false

_heading "⚠  Game.OS page is in DEMO MODE"
echo ""
echo "Issues found:"

if [ "${TOKEN_SET}" = "false" ]; then
    _warn "DATA_REPO_TOKEN was NOT injected into the deployed page."
    echo "    The site is running in demo mode (localStorage only)."
    echo ""
    echo "How to fix:"
    echo "  If you have already added/updated DATA_REPO_TOKEN in repository"
    echo "  secrets, just re-run the Deploy workflow — the new token will be"
    echo "  injected into the deployed page."
    echo ""
    echo "  If you have NOT set DATA_REPO_TOKEN yet:"
    echo "    Step 1 – Create a fine-grained PAT:"
    echo "      https://github.com/settings/tokens?type=beta"
    echo "      • Repository access : Only ${REPO_OWNER}/${DATA_REPO}"
    echo "      • Permission        : Contents → Read and write"
    echo ""
    echo "    Step 2 – Add the secret DATA_REPO_TOKEN:"
    echo "      https://github.com/${REPO_OWNER}/${REPO_NAME}/settings/secrets/actions"
    echo ""
    echo "    Step 3 – Re-run the Deploy workflow:"
    echo "      https://github.com/${REPO_OWNER}/${REPO_NAME}/actions/workflows/deploy.yml"
    NEEDS_DISPATCH=true
else
    _warn "Token is present in the page but the GitHub API check failed:"
    echo "    ${API_STATUS}"
    echo ""
    echo "How to fix:"
    case "${API_HTTP}" in
        401|403)
            NEEDS_NEW_PAT=true
            NEEDS_DISPATCH=true
            echo "  Step 1 – Regenerate your PAT (the old one is expired or revoked):"
            echo "    https://github.com/settings/tokens?type=beta"
            echo "    • Repository access : Only ${REPO_OWNER}/${DATA_REPO}"
            echo "    • Permission        : Contents → Read and write"
            echo ""
            echo "  Step 2 – Update the DATA_REPO_TOKEN secret:"
            echo "    https://github.com/${REPO_OWNER}/${REPO_NAME}/settings/secrets/actions"
            echo ""
            echo "  Step 3 – Re-run the Deploy workflow (this script can do that for you)."
            ;;
        404)
            NEEDS_NEW_PAT=true
            echo "  Step 1 – Create the private data repository (if it doesn't exist):"
            echo "    https://github.com/new"
            echo "    Name: ${DATA_REPO}   Visibility: Private"
            echo ""
            echo "  Step 2 – Ensure your PAT is scoped to that repository."
            echo ""
            echo "  Step 3 – Update DATA_REPO_TOKEN and re-run the Deploy workflow:"
            echo "    https://github.com/${REPO_OWNER}/${REPO_NAME}/actions/workflows/deploy.yml"
            ;;
        *)
            echo "  Check your internet connection and try again."
            echo "  If the problem persists, check: https://githubstatus.com"
            ;;
    esac
fi

# ── 7. Offer automatic fix via workflow dispatch ──────────────
#
# If the token has already been updated in repository secrets (the most
# common scenario: "PAT updated but site not back online yet"), triggering
# the Deploy workflow is the only step needed.  This script can do that
# automatically using a GitHub token that the user provides, then polls
# for completion and verifies the site is back — mirroring the VBScript
# version's auto-fix behaviour on Windows.

if [ "${NEEDS_DISPATCH}" = "true" ]; then
    echo ""
    echo "────────────────────────────────────────────────────────────────"
    echo "QUICK FIX: If you have already updated DATA_REPO_TOKEN in"
    echo "repository secrets, this script can automatically trigger the"
    echo "Deploy workflow and bring the site back online."
    echo ""
    echo "You will need a GitHub Personal Access Token with"
    echo "Actions: Read and write on ${REPO_OWNER}/${REPO_NAME}."
    echo "Get one at: https://github.com/settings/tokens?type=beta"
    echo ""
    echo -n "Enter that PAT (or press Enter to skip and open pages manually): "
    read -r DISPATCH_TOKEN
    DISPATCH_TOKEN="${DISPATCH_TOKEN//[[:space:]]/}"   # trim whitespace

    if [ -n "${DISPATCH_TOKEN}" ]; then
        DISPATCH_URL="https://api.github.com/repos/${REPO_OWNER}/${REPO_NAME}/actions/workflows/deploy.yml/dispatches"
        DISPATCH_HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 15 \
            -X POST "${DISPATCH_URL}" \
            -H "Authorization: Bearer ${DISPATCH_TOKEN}" \
            -H "Accept: application/vnd.github+json" \
            -H "X-GitHub-Api-Version: 2022-11-28" \
            -H "Content-Type: application/json" \
            -d '{"ref":"main"}' 2>/dev/null || echo "0")

        case "${DISPATCH_HTTP}" in
            204)
                _ok "Deploy workflow triggered successfully!"
                echo ""
                MAX_MINUTES=$(( MAX_POLL_ATTEMPTS * POLL_INTERVAL / 60 ))
                echo "The workflow is now running. This script will wait up to"
                echo "${MAX_MINUTES} minutes for it to complete, then verify the site."
                echo ""
                echo "Watch progress: https://github.com/${REPO_OWNER}/${REPO_NAME}/actions/workflows/deploy.yml"
                echo ""

                # Wait for GitHub to register the new run before first poll
                sleep "${WORKFLOW_REGISTRATION_DELAY}"

                RUNS_URL="https://api.github.com/repos/${REPO_OWNER}/${REPO_NAME}/actions/runs?workflow_id=deploy.yml&branch=main&per_page=1"
                RUN_STATUS=""
                RUN_CONCLUSION=""
                RUN_ID=""

                for TRY in $(seq 1 "${MAX_POLL_ATTEMPTS}"); do
                    sleep "${POLL_INTERVAL}"

                    RUNS_BODY=$(curl -sf --max-time 15 \
                        -H "Authorization: Bearer ${DISPATCH_TOKEN}" \
                        -H "Accept: application/vnd.github+json" \
                        -H "X-GitHub-Api-Version: 2022-11-28" \
                        "${RUNS_URL}" 2>/dev/null || echo "")

                    if [ -n "${RUNS_BODY}" ]; then
                        TOTAL=$(python3 -c "import sys,json; d=json.loads(sys.argv[1]); print(d.get('total_count',0))" "${RUNS_BODY}" 2>/dev/null || echo "0")
                        if [ "${TOTAL}" -gt 0 ] 2>/dev/null; then
                            RUN_STATUS=$(python3 -c "import sys,json; d=json.loads(sys.argv[1]); r=d.get('workflow_runs',[]); print(r[0].get('status','') if r else '')" "${RUNS_BODY}" 2>/dev/null || echo "")
                            RUN_CONCLUSION=$(python3 -c "import sys,json; d=json.loads(sys.argv[1]); r=d.get('workflow_runs',[]); print(r[0].get('conclusion','') if r else '')" "${RUNS_BODY}" 2>/dev/null || echo "")
                            RUN_ID=$(python3 -c "import sys,json; d=json.loads(sys.argv[1]); r=d.get('workflow_runs',[]); print(r[0].get('id','') if r else '')" "${RUNS_BODY}" 2>/dev/null || echo "")

                            if [ "${RUN_STATUS}" = "completed" ]; then
                                break
                            fi
                        fi
                    fi

                    ELAPSED=$(( TRY * POLL_INTERVAL ))
                    STATUS_DISPLAY="${RUN_STATUS:-queued/pending}"
                    echo "  [${ELAPSED}s] Status: ${STATUS_DISPLAY}"

                    # Periodic "still waiting" message (mirrors VBScript FEEDBACK_AFTER_ATTEMPTS)
                    if [ "${TRY}" -eq "${FEEDBACK_AFTER_ATTEMPTS}" ]; then
                        echo ""
                        _info "Still waiting for the Deploy workflow to complete…"
                        echo "  You can keep watching at:"
                        echo "  https://github.com/${REPO_OWNER}/${REPO_NAME}/actions/workflows/deploy.yml"
                        echo ""
                    fi
                done

                RUN_URL="https://github.com/${REPO_OWNER}/${REPO_NAME}/actions"
                if [ -n "${RUN_ID}" ]; then
                    RUN_URL="https://github.com/${REPO_OWNER}/${REPO_NAME}/actions/runs/${RUN_ID}"
                fi

                if [ "${RUN_STATUS}" != "completed" ]; then
                    _warn "Workflow is still running after ${MAX_MINUTES} minutes."
                    echo "  Check progress: ${RUN_URL}"
                    _open_url "${RUN_URL}"
                    exit 0
                fi

                if [ "${RUN_CONCLUSION}" != "success" ]; then
                    _error "Deploy workflow finished but did NOT succeed (conclusion: ${RUN_CONCLUSION})."
                    echo ""
                    echo "Check the workflow logs for errors:"
                    echo "  ${RUN_URL}"
                    echo ""
                    echo "Common causes:"
                    echo "  • DATA_REPO_TOKEN secret is still expired/invalid"
                    echo "  • Pages permissions not set to 'GitHub Actions'"
                    _open_url "${RUN_URL}"
                    exit 1
                fi

                # Verify the deployed page is now in GitHub mode
                sleep "${CDN_PROPAGATION_DELAY}"

                NEW_SCRIPT=$(curl -sf --max-time 20 "${SCRIPT_URL}" 2>/dev/null || echo "")
                NEW_ENCODED=$(python3 - <<'PYEOF'
import sys, re
m = re.search(r'const GITHUB_TOKEN_ENCODED\s*=\s*"([0-9a-fA-F]+)"', sys.stdin.read())
print(m.group(1) if m else '')
PYEOF
<<< "${NEW_SCRIPT}")

                if [ -n "${NEW_ENCODED}" ]; then
                    echo ""
                    _ok "Site is back ONLINE in GitHub mode!"
                    echo ""
                    echo "The Deploy workflow completed successfully and DATA_REPO_TOKEN"
                    echo "has been injected into the live page."
                    echo ""
                    echo "Open: ${PAGES_URL}"
                    _open_url "${PAGES_URL}"
                else
                    _warn "Workflow succeeded but token not yet visible in the page."
                    echo "  The CDN cache may still be propagating."
                    echo "  Wait a minute, then hard-refresh: ${PAGES_URL}"
                    echo "  If still in demo mode, re-run this script to diagnose."
                    _open_url "${PAGES_URL}"
                fi
                exit 0
                ;;
            401|403)
                _error "The token was rejected (HTTP ${DISPATCH_HTTP})."
                echo "Make sure it has Actions: Read and write on ${REPO_OWNER}/${REPO_NAME}."
                ;;
            422)
                _error "Workflow dispatch returned 422 (branch 'main' not found or workflow not enabled)."
                echo "Please trigger it manually at:"
                echo "  https://github.com/${REPO_OWNER}/${REPO_NAME}/actions/workflows/deploy.yml"
                ;;
            *)
                _error "Unexpected response from GitHub API (HTTP ${DISPATCH_HTTP})."
                ;;
        esac
    fi
fi

# ── Open relevant pages in browser ───────────────────────────
echo ""
if command -v xdg-open &>/dev/null || command -v open &>/dev/null; then
    echo "Opening relevant GitHub pages in your browser…"
fi

if [ "${NEEDS_NEW_PAT}" = "true" ] || [ "${TOKEN_SET}" = "false" ]; then
    _open_url "https://github.com/settings/tokens?type=beta"
    sleep 1
fi
if [ "${API_HTTP}" = "404" ]; then
    _open_url "https://github.com/new"
    sleep 1
fi
_open_url "https://github.com/${REPO_OWNER}/${REPO_NAME}/settings/secrets/actions"
sleep 1
_open_url "https://github.com/${REPO_OWNER}/${REPO_NAME}/actions/workflows/deploy.yml"

echo ""
echo "Links to open manually:"
if [ "${NEEDS_NEW_PAT}" = "true" ] || [ "${TOKEN_SET}" = "false" ]; then
    echo "  https://github.com/settings/tokens?type=beta"
fi
if [ "${API_HTTP}" = "404" ]; then
    echo "  https://github.com/new"
fi
echo "  https://github.com/${REPO_OWNER}/${REPO_NAME}/settings/secrets/actions"
echo "  https://github.com/${REPO_OWNER}/${REPO_NAME}/actions/workflows/deploy.yml"

exit 0
