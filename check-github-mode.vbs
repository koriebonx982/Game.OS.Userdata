' =============================================================
' Game.OS – GitHub Mode Checker & Fixer
' =============================================================
' Run this script on Windows to diagnose and fix the issue
' where the Game.OS webpage is stuck in demo mode.
'
' What this script does:
'   1. Downloads the deployed script.js from GitHub Pages
'   2. Checks whether DATA_REPO_TOKEN was injected at build time
'   3. XOR-decodes the token (same logic as script.js) and tests
'      GitHub API connectivity
'   4. Shows a root-cause diagnosis and opens the correct GitHub
'      pages so you can fix any problems found
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

Dim sToken : sToken = ""
If bTokenSet Then
    Dim nBytes : nBytes = Len(sEncoded) \ 2
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

' ── 6. Build issue list and fix instructions ──────────────────

Dim sIssues : sIssues = ""
Dim sFixes  : sFixes  = ""

If Not bTokenSet Then
    sIssues = sIssues & _
        "  • DATA_REPO_TOKEN was NOT injected into the deployed page." & vbCrLf & _
        "    The site is running in demo mode (localStorage only)." & vbCrLf

    sFixes = sFixes & _
        "Step 1 – Create or regenerate your fine-grained PAT:" & vbCrLf & _
        "  https://github.com/settings/tokens?type=beta" & vbCrLf & _
        "  • Repository access : Only " & REPO_OWNER & "/" & DATA_REPO & vbCrLf & _
        "  • Permission        : Contents → Read and write" & vbCrLf & _
        "  • Token name        : DATA_REPO_TOKEN (any name works)" & vbCrLf & vbCrLf & _
        "Step 2 – Add or update the repository secret:" & vbCrLf & _
        "  https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/settings/secrets/actions" & vbCrLf & _
        "  Secret name: DATA_REPO_TOKEN" & vbCrLf & vbCrLf & _
        "Step 3 – Re-run the Deploy workflow:" & vbCrLf & _
        "  https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions/workflows/deploy.yml" & vbCrLf & _
        "  Click 'Run workflow' → 'Run workflow' on the main branch." & vbCrLf

ElseIf Not bAPIok Then
    sIssues = sIssues & _
        "  • Token is present in the page but the GitHub API check failed:" & vbCrLf & _
        "    " & sAPIstatus & vbCrLf

    Select Case nAPI
        Case 401
            sFixes = sFixes & _
                "Step 1 – Regenerate your PAT (the old one is expired or revoked):" & vbCrLf & _
                "  https://github.com/settings/tokens?type=beta" & vbCrLf & _
                "  • Repository access : Only " & REPO_OWNER & "/" & DATA_REPO & vbCrLf & _
                "  • Permission        : Contents → Read and write" & vbCrLf & vbCrLf & _
                "Step 2 – Update the DATA_REPO_TOKEN secret:" & vbCrLf & _
                "  https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/settings/secrets/actions" & vbCrLf & vbCrLf & _
                "Step 3 – Re-run the Deploy workflow:" & vbCrLf & _
                "  https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions/workflows/deploy.yml" & vbCrLf

        Case 403
            sFixes = sFixes & _
                "Step 1 – Regenerate your PAT with the correct permission:" & vbCrLf & _
                "  https://github.com/settings/tokens?type=beta" & vbCrLf & _
                "  • Repository access : Only " & REPO_OWNER & "/" & DATA_REPO & vbCrLf & _
                "  • Permission        : Contents → Read and write" & vbCrLf & vbCrLf & _
                "Step 2 – Update DATA_REPO_TOKEN and re-run the Deploy workflow." & vbCrLf

        Case 404
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

' ── 7. Show report and offer to open fix pages ────────────────

Dim sReport : sReport = _
    "The Game.OS page is stuck in DEMO MODE." & vbCrLf & vbCrLf & _
    "Issues found:" & vbCrLf & sIssues & vbCrLf & _
    "How to fix:" & vbCrLf & sFixes & vbCrLf & _
    "Open the relevant GitHub pages now?"

Dim nAns : nAns = MsgBox(sReport, 36, "Game.OS Checker – Demo Mode Detected")

If nAns = 6 Then  ' vbYes
    If Not bTokenSet Or nAPI = 401 Or nAPI = 403 Then
        ' Open PAT creation page
        oShell.Run "https://github.com/settings/tokens?type=beta", 1, False
        WScript.Sleep 800
    End If
    If nAPI = 404 Then
        ' Open new repository page
        oShell.Run "https://github.com/new", 1, False
        WScript.Sleep 800
    End If
    ' Open repository secrets page
    oShell.Run "https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/settings/secrets/actions", 1, False
    WScript.Sleep 800
    ' Open Deploy workflow page
    oShell.Run "https://github.com/" & REPO_OWNER & "/" & REPO_NAME & "/actions/workflows/deploy.yml", 1, False
End If

WScript.Quit 0
