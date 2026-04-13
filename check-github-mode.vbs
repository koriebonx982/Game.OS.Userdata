' =============================================================
' Game.OS – GitHub Mode Checker & Auto-Fixer
' =============================================================
' Run this script on Windows to diagnose AND fix the issue
' where the Game.OS webpage is stuck in demo mode.
'
' What this script does:
'   1. Downloads the deployed script.js from GitHub Pages
'   2. Checks whether DATA_REPO_TOKEN was injected at build time
'   3. XOR-decodes the token (same logic as script.js) and tests
'      GitHub API connectivity
'   4. If the site is stuck in demo mode but DATA_REPO_TOKEN has
'      been updated in repository secrets, it can automatically
'      trigger the Deploy workflow, wait for it to finish, and
'      verify the site is back online – no browser needed.
'   5. Shows a root-cause diagnosis and opens the correct GitHub
'      pages so you can fix any remaining problems
'
' How to run:
'   Double-click this file in Windows Explorer, or run:
'     wscript check-github-mode.vbs
' =============================================================

Option Explicit

Const REPO_OWNER  = "Koriebonx98"
Const REPO_NAME   = "Game.OS.Userdata"
Const DATA_REPO   = "Game.OS.Private.Data"
Const XOR_KEY     = "GameOS_KEY"
Const PAGES_URL   = "https://Koriebonx98.github.io/Game.OS.Userdata"

' Workflow polling configuration
Const WORKFLOW_REGISTRATION_DELAY_MS = 5000   ' ms to wait after dispatch before first poll
Const CDN_PROPAGATION_DELAY_MS       = 8000   ' ms to wait after deploy before re-fetching page
Const POLL_INTERVAL_MS               = 10000  ' ms between each status poll
Const MAX_POLL_ATTEMPTS              = 60     ' 60 × 10 s = 10 minutes maximum wait
Const FEEDBACK_AFTER_ATTEMPTS        = 6      ' show "still waiting" message after this many attempts

Dim oShell : Set oShell = CreateObject("WScript.Shell")

' ── 1. Fetch the deployed script.js ──────────────────────────

Dim sScriptUrl : sScriptUrl = PAGES_URL & "/script.js"

Dim oHTTP : Set oHTTP = CreateObject("MSXML2.XMLHTTP.6.0")
On Error Resume Next
oHTTP.Open "GET", sScriptUrl, False
oHTTP.Send
Dim nHTTP : nHTTP = oHTTP.Status
If Err.Number <> 0 Or nHTTP <> 200 Then
    Dim sNetErr : sNetErr = ""
    If Err.Number <> 0 Then sNetErr = " (" & Err.Description & ")"
    MsgBox "Could not download the deployed script.js." & vbCrLf & vbCrLf & _
           "URL  : " & sScriptUrl & vbCrLf & _
           "HTTP : " & nHTTP & sNetErr & vbCrLf & vbCrLf & _
           "Possible causes:" & vbCrLf & _
           "  • GitHub Pages has not been deployed yet" & vbCrLf & _
           "  • Pages source is not set to 'GitHub Actions'" & vbCrLf & _
           "  • No internet connection" & vbCrLf & vbCrLf & _
           "Fix: go to  github.com/" & REPO_OWNER & "/" & REPO_NAME & "/settings/pages" & vbCrLf & _
           "and confirm Source is set to 'GitHub Actions', then trigger the Deploy workflow.", _
           16, "Game.OS Checker – Page Not Reachable"
    WScript.Quit 1
End If
On Error GoTo 0

Dim sScript : sScript = oHTTP.ResponseText

' ── 2. Check whether GITHUB_TOKEN_ENCODED is present ─────────

Dim oRE : Set oRE = CreateObject("VBScript.RegExp")
oRE.Global = False

' Match a non-empty double-quoted hex string (injected by deploy.yml)
oRE.Pattern = "const GITHUB_TOKEN_ENCODED\s*=\s*""([0-9a-fA-F]+)"""
Dim oM : Set oM = oRE.Execute(sScript)

Dim sEncoded : sEncoded = ""
Dim bTokenSet : bTokenSet = False
If oM.Count > 0 Then
    sEncoded  = oM(0).SubMatches(0)
    bTokenSet = (Len(sEncoded) > 0)
End If

' ── 3. XOR-decode the token (mirrors script.js runtime logic) ─

Dim sToken  : sToken  = ""
Dim nBytes  : nBytes  = 0   ' used in "all good" confirmation message below
If bTokenSet Then
    nBytes = Len(sEncoded) \ 2
    Dim i
    For i = 0 To nBytes - 1
        Dim bByte : bByte = CInt("&H" & Mid(sEncoded, i * 2 + 1, 2))
        Dim bKey  : bKey  = Asc(Mid(XOR_KEY, (i Mod Len(XOR_KEY)) + 1, 1))
        sToken = sToken & Chr(bByte Xor bKey)
    Next
End If

' ── 4. Test GitHub API connectivity ──────────────────────────

Dim bAPIok    : bAPIok    = False
Dim sAPIstatus: sAPIstatus = "not tested (no token injected)"
Dim nAPI      : nAPI      = 0

If bTokenSet And Len(sToken) > 0 Then
    Dim sAPIurl : sAPIurl = "https://api.github.com/repos/" & REPO_OWNER & "/" & DATA_REPO
    Dim oHTTP2  : Set oHTTP2 = CreateObject("MSXML2.XMLHTTP.6.0")
    On Error Resume Next
    oHTTP2.Open "GET", sAPIurl, False
    oHTTP2.SetRequestHeader "Authorization",        "Bearer " & sToken
    oHTTP2.SetRequestHeader "Accept",               "application/vnd.github+json"
    oHTTP2.SetRequestHeader "X-GitHub-Api-Version", "2022-11-28"
    oHTTP2.Send
    nAPI = oHTTP2.Status
    If Err.Number <> 0 Then
        sAPIstatus = "Network error: " & Err.Description
    ElseIf nAPI = 200 Then
        bAPIok     = True
        sAPIstatus = "200 OK – repository is accessible"
    ElseIf nAPI = 401 Then
        sAPIstatus = "401 Unauthorized – token is invalid or expired"
    ElseIf nAPI = 403 Then
        sAPIstatus = "403 Forbidden – token lacks Contents: Read and write permission"
    ElseIf nAPI = 404 Then
        sAPIstatus = "404 Not Found – repository does not exist or token has no access to it"
    Else
        sAPIstatus = "HTTP " & nAPI
    End If
    On Error GoTo 0
End If

' ── 5. All good? ──────────────────────────────────────────────

If bTokenSet And bAPIok Then
    MsgBox "✅  GitHub mode is fully operational." & vbCrLf & vbCrLf & _
           "  Deployed page : " & PAGES_URL & vbCrLf & _
           "  Token         : ✅ Injected (" & nBytes & " bytes)" & vbCrLf & _
           "  GitHub API    : ✅ " & sAPIstatus & vbCrLf & vbCrLf & _
           "If you still cannot log in, try:" & vbCrLf & _
           "  • Hard-refresh the page (Ctrl+Shift+R)" & vbCrLf & _
           "  • Clear browser site data for the Game.OS page" & vbCrLf & _
           "  • Use the Sign Up page to recreate your account", _
           64, "Game.OS Checker – All Good"
    WScript.Quit 0
End If

' ── 6. Determine root cause ───────────────────────────────────

Dim sIssues : sIssues = ""
Dim sFixes  : sFixes  = ""
Dim bNeedsNewPAT : bNeedsNewPAT = False
Dim bNeedsDispatch : bNeedsDispatch = False  ' token updated in secret, just needs redeploy

If Not bTokenSet Then
    ' No token in the deployed page at all – either secret was never set,
    ' or it was updated but the workflow hasn't been re-run yet.
    sIssues = sIssues & _
        "  • DATA_REPO_TOKEN was NOT injected into the deployed page." & vbCrLf & _
        "    The site is running in demo mode (localStorage only)." & vbCrLf

    sFixes = sFixes & _
        "If you have already added/updated DATA_REPO_TOKEN in repository secrets," & vbCrLf & _
        "the fix is simply to re-run the Deploy workflow so the new token is" & vbCrLf & _
        "injected into the deployed page." & vbCrLf & vbCrLf & _
        "If you have NOT set DATA_REPO_TOKEN yet:" & vbCrLf & _
        "  Step 1 – Create a fine-grained PAT:" & vbCrLf & _
        "    https://github.com/settings/tokens?type=beta" & vbCrLf & _
        "    • Repository access : Only " & REPO_OWNER & "/" & DATA_REPO & vbCrLf & _
        "    • Permission        : Contents → Read and write" & vbCrLf & vbCrLf & _
        "  Step 2 – Add the secret DATA_REPO_TOKEN:" & vbCrLf & _
        "    https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/settings/secrets/actions" & vbCrLf & vbCrLf & _
        "  Step 3 – Re-run the Deploy workflow:" & vbCrLf & _
        "    https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions/workflows/deploy.yml" & vbCrLf

    bNeedsDispatch = True  ' either way, redeploying is the fix

ElseIf Not bAPIok Then
    sIssues = sIssues & _
        "  • Token is present in the page but the GitHub API check failed:" & vbCrLf & _
        "    " & sAPIstatus & vbCrLf

    Select Case nAPI
        Case 401
            bNeedsNewPAT  = True
            bNeedsDispatch = True
            sFixes = sFixes & _
                "Step 1 – Regenerate your PAT (the old one is expired or revoked):" & vbCrLf & _
                "  https://github.com/settings/tokens?type=beta" & vbCrLf & _
                "  • Repository access : Only " & REPO_OWNER & "/" & DATA_REPO & vbCrLf & _
                "  • Permission        : Contents → Read and write" & vbCrLf & vbCrLf & _
                "Step 2 – Update the DATA_REPO_TOKEN secret:" & vbCrLf & _
                "  https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/settings/secrets/actions" & vbCrLf & vbCrLf & _
                "Step 3 – Re-run the Deploy workflow (this script can do that for you)." & vbCrLf

        Case 403
            bNeedsNewPAT  = True
            bNeedsDispatch = True
            sFixes = sFixes & _
                "Step 1 – Regenerate your PAT with the correct permission:" & vbCrLf & _
                "  https://github.com/settings/tokens?type=beta" & vbCrLf & _
                "  • Repository access : Only " & REPO_OWNER & "/" & DATA_REPO & vbCrLf & _
                "  • Permission        : Contents → Read and write" & vbCrLf & vbCrLf & _
                "Step 2 – Update DATA_REPO_TOKEN and re-run the Deploy workflow." & vbCrLf

        Case 404
            bNeedsNewPAT  = True
            sFixes = sFixes & _
                "Step 1 – Create the private data repository (if it doesn't exist):" & vbCrLf & _
                "  https://github.com/new" & vbCrLf & _
                "  Name: " & DATA_REPO & "   Visibility: Private" & vbCrLf & vbCrLf & _
                "Step 2 – Ensure your PAT is scoped to that repository." & vbCrLf & vbCrLf & _
                "Step 3 – Update DATA_REPO_TOKEN and re-run the Deploy workflow." & vbCrLf

        Case Else
            sFixes = sFixes & _
                "Check your internet connection and try again." & vbCrLf & _
                "If the problem persists, check the GitHub status page: https://githubstatus.com" & vbCrLf
    End Select
End If

' ── 7. Offer automatic fix via workflow dispatch ──────────────
'
' If the token has already been updated in repository secrets (the most common
' scenario: "PAT has been updated but site not online"), triggering the Deploy
' workflow is the only step needed. This script can do that automatically using
' a GitHub token that the user provides.

Dim sReport : sReport = _
    "The Game.OS page is stuck in DEMO MODE." & vbCrLf & vbCrLf & _
    "Issues found:" & vbCrLf & sIssues & vbCrLf & _
    "How to fix:" & vbCrLf & sFixes

If bNeedsDispatch Then
    sReport = sReport & vbCrLf & _
        "──────────────────────────────────────────────────────────────" & vbCrLf & _
        "QUICK FIX: If you have already updated DATA_REPO_TOKEN in" & vbCrLf & _
        "repository secrets, click YES to let this script automatically" & vbCrLf & _
        "trigger the Deploy workflow and bring the site back online." & vbCrLf & _
        "(You will need a GitHub Personal Access Token with Actions: write" & vbCrLf & _
        " on this repository to trigger the workflow.)" & vbCrLf & vbCrLf & _
        "Click NO to open the relevant GitHub pages instead."
Else
    sReport = sReport & vbCrLf & "Open the relevant GitHub pages now?"
End If

Dim nAns : nAns = MsgBox(sReport, 36, "Game.OS Checker – Demo Mode Detected")

If nAns = 6 Then  ' vbYes

    If bNeedsDispatch Then
        ' ── Attempt automatic workflow dispatch ───────────────────

        Dim sDispatchToken : sDispatchToken = InputBox( _
            "Enter a GitHub Personal Access Token to trigger the Deploy workflow." & vbCrLf & vbCrLf & _
            "The token needs Actions: write permission on:" & vbCrLf & _
            "  " & REPO_OWNER & "/" & REPO_NAME & vbCrLf & vbCrLf & _
            "Get one at: https://github.com/settings/tokens?type=beta" & vbCrLf & _
            "  (Repository access: " & REPO_NAME & "  |  Actions: Read and write)" & vbCrLf & vbCrLf & _
            "If you prefer to trigger it manually, cancel and use:" & vbCrLf & _
            "  https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions/workflows/deploy.yml", _
            "Game.OS – Trigger Deploy Workflow", "")

        If Len(Trim(sDispatchToken)) = 0 Then
            ' User cancelled – fall back to opening pages
            GoTo OpenPages
        End If

        sDispatchToken = Trim(sDispatchToken)

        ' POST workflow dispatch
        Dim sDispatchUrl : sDispatchUrl = _
            "https://api.github.com/repos/" & REPO_OWNER & "/" & REPO_NAME & _
            "/actions/workflows/deploy.yml/dispatches"

        Dim oHTTP3 : Set oHTTP3 = CreateObject("MSXML2.XMLHTTP.6.0")
        On Error Resume Next
        oHTTP3.Open "POST", sDispatchUrl, False
        oHTTP3.SetRequestHeader "Authorization",        "Bearer " & sDispatchToken
        oHTTP3.SetRequestHeader "Accept",               "application/vnd.github+json"
        oHTTP3.SetRequestHeader "X-GitHub-Api-Version", "2022-11-28"
        oHTTP3.SetRequestHeader "Content-Type",         "application/json"
        oHTTP3.Send "{""ref"":""main""}"
        Dim nDispatch : nDispatch = oHTTP3.Status
        Dim sDispatchErr : sDispatchErr = ""
        If Err.Number <> 0 Then sDispatchErr = Err.Description
        On Error GoTo 0

        If sDispatchErr <> "" Then
            MsgBox "Network error triggering workflow: " & sDispatchErr & vbCrLf & vbCrLf & _
                   "Please trigger it manually:" & vbCrLf & _
                   "  https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions/workflows/deploy.yml", _
                   16, "Game.OS – Dispatch Failed"
            GoTo OpenPages
        ElseIf nDispatch = 204 Then
            ' 204 No Content = dispatch accepted
        ElseIf nDispatch = 401 Or nDispatch = 403 Then
            MsgBox "The token you entered was rejected (HTTP " & nDispatch & ")." & vbCrLf & vbCrLf & _
                   "Make sure the token has Actions: Read and write on:" & vbCrLf & _
                   "  " & REPO_OWNER & "/" & REPO_NAME & vbCrLf & vbCrLf & _
                   "Please trigger the workflow manually:" & vbCrLf & _
                   "  https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions/workflows/deploy.yml", _
                   16, "Game.OS – Dispatch Failed"
            GoTo OpenPages
        ElseIf nDispatch = 422 Then
            MsgBox "Workflow dispatch returned 422 (branch 'main' not found or workflow not found)." & vbCrLf & _
                   "Please trigger it manually from:" & vbCrLf & _
                   "  https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions/workflows/deploy.yml", _
                   16, "Game.OS – Dispatch Failed"
            GoTo OpenPages
        Else
            MsgBox "Unexpected response from GitHub API (HTTP " & nDispatch & ")." & vbCrLf & _
                   "Please trigger the Deploy workflow manually.", _
                   48, "Game.OS – Dispatch Warning"
            GoTo OpenPages
        End If

        ' ── Poll for completion ────────────────────────────────────

        Dim nMaxMinutes : nMaxMinutes = (MAX_POLL_ATTEMPTS * POLL_INTERVAL_MS) \ 60000
        MsgBox "✅ Deploy workflow triggered successfully!" & vbCrLf & vbCrLf & _
               "The workflow is now running. Click OK and this script will" & vbCrLf & _
               "wait up to " & nMaxMinutes & " minutes for it to complete, then verify the" & vbCrLf & _
               "site is back online." & vbCrLf & vbCrLf & _
               "You can also watch progress at:" & vbCrLf & _
               "  https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions/workflows/deploy.yml", _
               64, "Game.OS – Workflow Triggered"

        ' Wait for GitHub to register the new run before first poll
        WScript.Sleep WORKFLOW_REGISTRATION_DELAY_MS

        Dim sRunsUrl : sRunsUrl = _
            "https://api.github.com/repos/" & REPO_OWNER & "/" & REPO_NAME & _
            "/actions/runs?workflow_id=deploy.yml&branch=main&per_page=1"

        ' One regex object reused for all JSON field extractions during polling
        Dim oREpoll : Set oREpoll = CreateObject("VBScript.RegExp")
        oREpoll.Global = False

        Dim nTry         : nTry         = 0
        Dim sRunStatus   : sRunStatus   = ""
        Dim sConclusion  : sConclusion  = ""
        Dim sLatestRunId : sLatestRunId = ""

        Do While nTry < MAX_POLL_ATTEMPTS
            WScript.Sleep POLL_INTERVAL_MS

            Dim oHTTP4 : Set oHTTP4 = CreateObject("MSXML2.XMLHTTP.6.0")
            On Error Resume Next
            oHTTP4.Open "GET", sRunsUrl, False
            oHTTP4.SetRequestHeader "Authorization",        "Bearer " & sDispatchToken
            oHTTP4.SetRequestHeader "Accept",               "application/vnd.github+json"
            oHTTP4.SetRequestHeader "X-GitHub-Api-Version", "2022-11-28"
            oHTTP4.Send
            Dim nRunsHTTP : nRunsHTTP = oHTTP4.Status
            Dim sRunsBody : sRunsBody = ""
            If Err.Number = 0 And nRunsHTTP = 200 Then
                sRunsBody = oHTTP4.ResponseText
            End If
            On Error GoTo 0

            If Len(sRunsBody) > 0 Then
                oREpoll.Pattern = """total_count""\s*:\s*(\d+)"
                Dim oMcount : Set oMcount = oREpoll.Execute(sRunsBody)
                If oMcount.Count > 0 And CLng(oMcount(0).SubMatches(0)) > 0 Then

                    oREpoll.Pattern = """id""\s*:\s*(\d+)"
                    Dim oMrunId : Set oMrunId = oREpoll.Execute(sRunsBody)
                    If oMrunId.Count > 0 Then sLatestRunId = oMrunId(0).SubMatches(0)

                    oREpoll.Pattern = """status""\s*:\s*""([^""]+)"""
                    Dim oMstatus : Set oMstatus = oREpoll.Execute(sRunsBody)
                    If oMstatus.Count > 0 Then sRunStatus = oMstatus(0).SubMatches(0)

                    oREpoll.Pattern = """conclusion""\s*:\s*""([^""]+)"""
                    Dim oMconc : Set oMconc = oREpoll.Execute(sRunsBody)
                    If oMconc.Count > 0 Then sConclusion = oMconc(0).SubMatches(0)

                    If sRunStatus = "completed" Then Exit Do
                End If
            End If

            nTry = nTry + 1

            ' Periodic progress notification so the user knows we haven't stalled
            If nTry = FEEDBACK_AFTER_ATTEMPTS Then
                Dim nElapsedSec : nElapsedSec = (nTry * POLL_INTERVAL_MS) \ 1000
                Dim sStatusDisplay : sStatusDisplay = sRunStatus
                If Len(sStatusDisplay) = 0 Then sStatusDisplay = "queued/pending"
                MsgBox "Still waiting for the Deploy workflow to complete…" & vbCrLf & _
                       "  Elapsed: ~" & nElapsedSec & " seconds  |  Status: " & _
                       sStatusDisplay & vbCrLf & vbCrLf & _
                       "Click OK to keep waiting, or check progress at:" & vbCrLf & _
                       "  https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions/workflows/deploy.yml", _
                       64, "Game.OS – Still Waiting"
            End If
        Loop

        ' ── Report workflow outcome ────────────────────────────────

        Dim sRunUrl : sRunUrl = "https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions"
        If Len(sLatestRunId) > 0 Then
            sRunUrl = "https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions/runs/" & sLatestRunId
        End If

        If sRunStatus <> "completed" Then
            MsgBox "The workflow is still running after " & nMaxMinutes & " minutes." & vbCrLf & vbCrLf & _
                   "Please check its progress at:" & vbCrLf & "  " & sRunUrl & vbCrLf & vbCrLf & _
                   "Once it completes, hard-refresh the site (Ctrl+Shift+R).", _
                   48, "Game.OS – Workflow Still Running"
            oShell.Run sRunUrl, 1, False
            WScript.Quit 0
        End If

        If sConclusion <> "success" Then
            MsgBox "The Deploy workflow finished but did NOT succeed." & vbCrLf & _
                   "Conclusion: " & sConclusion & vbCrLf & vbCrLf & _
                   "Please check the workflow logs for errors:" & vbCrLf & _
                   "  " & sRunUrl & vbCrLf & vbCrLf & _
                   "Common causes:" & vbCrLf & _
                   "  • DATA_REPO_TOKEN secret is still expired/invalid" & vbCrLf & _
                   "  • Pages permissions not set to 'GitHub Actions'", _
                   16, "Game.OS – Workflow Failed"
            oShell.Run sRunUrl, 1, False
            WScript.Quit 1
        End If

        ' ── Verify the deployed page is now in GitHub mode ────────

        ' Wait for Pages CDN to propagate
        WScript.Sleep CDN_PROPAGATION_DELAY_MS

        Dim oHTTP5 : Set oHTTP5 = CreateObject("MSXML2.XMLHTTP.6.0")
        Dim sScript2 : sScript2 = ""
        On Error Resume Next
        oHTTP5.Open "GET", sScriptUrl, False
        oHTTP5.Send
        If Err.Number = 0 And oHTTP5.Status = 200 Then
            sScript2 = oHTTP5.ResponseText
        End If
        On Error GoTo 0

        ' Reuse existing oRE for post-deploy verification
        Dim bNowLive : bNowLive = False
        If Len(sScript2) > 0 Then
            oRE.Pattern = "const GITHUB_TOKEN_ENCODED\s*=\s*""([0-9a-fA-F]+)"""
            Dim oM2 : Set oM2 = oRE.Execute(sScript2)
            If oM2.Count > 0 And Len(oM2(0).SubMatches(0)) > 0 Then
                bNowLive = True
            End If
        End If

        If bNowLive Then
            Dim nOpen : nOpen = MsgBox( _
                "✅  Site is back ONLINE in GitHub mode!" & vbCrLf & vbCrLf & _
                "The Deploy workflow completed successfully and DATA_REPO_TOKEN" & vbCrLf & _
                "has been injected into the live page." & vbCrLf & vbCrLf & _
                "Open the Game.OS site now?", _
                36, "Game.OS – Back Online")
            If nOpen = 6 Then oShell.Run PAGES_URL, 1, False
        Else
            MsgBox "The workflow succeeded but the token does not appear in the" & vbCrLf & _
                   "deployed page yet (CDN cache may still be propagating)." & vbCrLf & vbCrLf & _
                   "Please wait a minute, then hard-refresh the site:" & vbCrLf & _
                   "  " & PAGES_URL & vbCrLf & vbCrLf & _
                   "If the site is still in demo mode after several minutes," & vbCrLf & _
                   "re-run this script to diagnose the remaining issue.", _
                   48, "Game.OS – CDN Propagating"
            oShell.Run PAGES_URL, 1, False
        End If

        WScript.Quit 0
    End If  ' bNeedsDispatch

OpenPages:

    If bNeedsNewPAT Or Not bTokenSet Then
        oShell.Run "https://github.com/settings/tokens?type=beta", 1, False
        WScript.Sleep 800
    End If
    If nAPI = 404 Then
        oShell.Run "https://github.com/new", 1, False
        WScript.Sleep 800
    End If
    oShell.Run "https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/settings/secrets/actions", 1, False
    WScript.Sleep 800
    oShell.Run "https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions/workflows/deploy.yml", 1, False

End If  ' nAns = vbYes

WScript.Quit 0
