# Game.OS Launcher — Graphical PC App

A **graphical Windows/Linux/macOS PC game launcher** for Game.OS — designed like  
**Xbox Dashboard / PlayStation 5 / Steam Big Picture / Playnite**.

Built with **.NET 8** + [Avalonia UI](https://avaloniaui.net/) (cross-platform WPF-style GUI).

> **Same backend as the website** — the launcher authenticates and stores data directly in the
> same private GitHub repository that the web frontend uses.  No separate Node.js server is needed.

---

## Visual Studio

Open **`Game.OS.Userdata.sln`** in the repository root to load all three projects in
**Visual Studio 2022+** with a single double-click.

| Project | Type | Purpose |
|---|---|---|
| `GameLauncher` | WinExe / Avalonia | The graphical launcher application |
| `LoginAuth.Tests` | Console | Proves C# login ≡ web frontend login |
| `GameScanner.Tests` | Console | Proves local game/repack detection |

All three are configured for Debug / Release × Any CPU.

---

## Login Flow — Same as the Website

The C# launcher authenticates with **exactly the same method** as the web frontend:

| Step | Web frontend (`script.js`) | C# launcher (`GitHubDataService.cs`) |
|---|---|---|
| Hash algorithm | PBKDF2-SHA256, 100 000 iter | PBKDF2-SHA256, 100 000 iter |
| Salt | `{username}:gameos` | `{username}:gameos` |
| Storage | GitHub data repo | GitHub data repo |
| Bcrypt support | ✅ backend-created accounts | ✅ `BCrypt.Net.BCrypt.Verify()` |

![Same Login — Web vs C# Launcher](../Design/Screenshots/screenshot_launcher_compare.png)

> *Left: web browser login · Right: C# launcher — both sign in as the same account using identical PBKDF2-SHA256 password hashing*

### Login Auth Tests — C# ≡ Web

The `LoginAuth.Tests` project proves this parity automatically.  Run it from the repo root:

```bash
cd LoginAuth.Tests && dotnet run
```

All 14 checks pass automatically (no secrets required):
- PBKDF2 hashes match Node.js reference vectors byte-for-byte
- Username salt is case-insensitive (matches JS `username.toLowerCase()`)
- Bcrypt hashes (Node.js backend accounts) are detected and verified correctly
- Both hash types accept correct passwords and reject wrong ones

To also run the **live backend test** (Test 5 — signs in to the real GitHub data repository):

```bash
GAMEOS_GITHUB_TOKEN=<DATA_REPO_TOKEN> \
GAMEOS_TEST_USERNAME=Koriebonx98 \
GAMEOS_TEST_PASSWORD=<your-password> \
  dotnet run --project LoginAuth.Tests
```

The CI workflow `.github/workflows/build-csharp-launcher.yml` runs this test automatically on
every push to `main` using the `DATA_REPO_TOKEN` and `GAMEOS_TEST_PASSWORD` repository secrets.

![C# Launcher — Live Login Test Output](../Design/Screenshots/screenshot_login_auth_live_test.png)

> *All 14 tests pass (Tests 1–4 verify hash parity; Test 5 confirms live backend login for Koriebonx98)*

### Login Success — C# Launcher Dashboard

After a successful login the C# launcher shows the same account data fetched live from GitHub:

![C# Launcher — Login Success](../Design/Screenshots/screenshot_launcher_login_success.png)

> *Dashboard after login: account name, game library, achievements, and platform count all loaded from the real GitHub data repository*

---

## Screenshots

| 🔐 Login | 🏠 Dashboard |
|---|---|
| ![Login](../Design/Screenshots/screenshot_login.png) | ![Dashboard](../Design/Screenshots/screenshot_dashboard.png) |

| 🎮 My Library | 🛒 Games Store |
|---|---|
| ![Library](../Design/Screenshots/screenshot_library.png) | ![Store](../Design/Screenshots/screenshot_store.png) |

| 🎯 Game Details | 👤 Profile |
|---|---|
| ![Game Detail](../Design/Screenshots/screenshot_gamedetail.png) | ![Profile](../Design/Screenshots/screenshot_profile.png) |

| 👥 Friends | 🔍 Local Game Detection |
|---|---|
| ![Friends](../Design/Screenshots/screenshot_friends.png) | ![Detected on Drive](../Design/Screenshots/screenshot_library_detected.png) |

---

## Features

| Screen | What it does |
|---|---|
| 🔐 **Login / Register** | Dark-themed sign-in form; **Remember me** caches your session token between launches (same as the website) |
| 🏠 **Dashboard / Home** | Stats tiles, featured game hero banner, recently-added game cards, recent achievements |
| 🎮 **My Library** | Game cover cards loaded from the cloud, platform filter chips, search bar, star ratings |
| 🎯 **Game Details** | Full-screen overlay with cover art, description, rating, screenshots carousel, play button |
| 🛒 **Games Store** | Featured titles carousel, browse all, genre filter, search, one-click add to library |
| 👥 **Friends** | Live friend list with online/away/offline presence, pending friend requests |
| 👤 **Profile** | Avatar, stats, full achievements list, LIVE / ADMIN badge |
| 🛡️ **Admin Mode** | When logged in as `Admin.GameOS`, a catalog management panel appears in the store |

---

## Quick Start

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download) or later
- A Game.OS account (create one at the [web frontend](../README.md) — same account works in the launcher)

### Option A — Download the pre-built launcher (easiest)

Go to **GitHub Actions → Build & Live-Login Test → GameOS-Launcher-win-x64 artifact** and
download the pre-built Windows exe. It has the backend URL baked in and works immediately.

### Option B — Run from Visual Studio (developers)

1. **Start the local backend server:**
   ```bash
   cd backend
   npm install
   node index.js
   # → Server running at http://localhost:3000
   ```
   The backend needs `GITHUB_TOKEN`, `REPO_OWNER`, and `REPO_NAME` — see `backend/.env.example`.

2. **Open Visual Studio** and load `Game.OS.Userdata.sln` from the repository root.

3. **Press ▶ (Run)** with the **GameLauncher** project selected.

   Visual Studio automatically reads `GameLauncher/Properties/launchSettings.json` and sets
   `GAMEOS_BACKEND_URL=http://localhost:3000` before launching — **no manual env-var setup needed**.

4. Sign in with your Game.OS account and the launcher will connect to the local backend.

### Option C — Run from the command line

```bash
cd GameLauncher
dotnet run
# launchSettings.json auto-sets GAMEOS_BACKEND_URL=http://localhost:3000
```

The `Properties/launchSettings.json` is read by both Visual Studio and `dotnet run`, so the
`GAMEOS_BACKEND_URL` environment variable is set automatically — no manual configuration needed.

### How token access works — same mechanism as the website

The web frontend and the C# launcher use **identical authentication**:

| | Web frontend | C# launcher |
|---|---|---|
| Token storage | `GITHUB_TOKEN_ENCODED = '...'` in `script.js` | `gameos-token.dat` next to the executable |
| Token encoding | XOR-hex, key `GameOS_KEY` (by `deploy.yml`) | XOR-hex, key `GameOS_KEY` (by `build-csharp-launcher.yml`) |
| Decode method | `bytes.map((h,i) => chr(parseInt(h,16) ^ key[i%9]))` | `GitHubDataService.DecodeXorToken()` — same formula |
| API calls | `fetch("https://api.github.com/repos/…")` | `HttpClient.GetAsync("https://api.github.com/repos/…")` |
| Password hash | PBKDF2-SHA256, 100,000 iter, salt `{user_lower}:gameos` | Same — `Rfc2898DeriveBytes` |

The `build-csharp-launcher.yml` GitHub Actions workflow:
1. Reads `DATA_REPO_TOKEN` from repository secrets (same secret used by `deploy.yml`)
2. XOR-encodes it with key `GameOS_KEY`
3. Writes the encoded string to `GameLauncher/gameos-token.dat` before building
4. Reads `GAMEOS_BACKEND_URL` from secrets and writes it to `GameLauncher/gameos-backend.url`
5. Builds and publishes the app — both files are bundled alongside the executable
6. Runs the live login test for `Koriebonx98` to confirm everything works

The empty placeholder files are committed to the repo. The real values are only present inside
the CI runner during the build and are never committed back.

![Build & Login Architecture](../Design/Screenshots/screenshot_build_and_login.png)

### Configuration

The launcher supports two authentication modes and auto-detects which one to use:

#### Mode 1 — Backend REST API (preferred, no GitHub PAT needed)

The `Properties/launchSettings.json` automatically sets `GAMEOS_BACKEND_URL=http://localhost:3000`
when running with Visual Studio or `dotnet run` — **no manual configuration needed**.

To use a different backend URL, set the environment variable:

```bash
# Windows
set GAMEOS_BACKEND_URL=https://gameos.up.railway.app
dotnet run

# Linux / macOS
GAMEOS_BACKEND_URL=https://gameos.up.railway.app dotnet run
```

Or write the URL to `gameos-backend.url` in the project/executable folder.

The launcher calls `POST {url}/api/auth/token` with `{ username, password }` — **exactly the
same endpoint the web frontend calls**.  The backend server holds the GitHub PAT; the launcher
never needs to store or bundle one.

#### Mode 2 — GitHub-direct (fallback, requires a GitHub PAT)

When `GAMEOS_BACKEND_URL` is not set, the launcher falls back to calling the GitHub API
directly (mirroring the web frontend's GitHub-direct mode).  This requires a GitHub PAT:

| Variable | Default | Purpose |
|---|---|---|
| `GAMEOS_BACKEND_URL` | `http://localhost:3000` (via launchSettings.json) | **Preferred** — backend server URL; no PAT needed |
| `GAMEOS_DATA_REPO_OWNER` | `Koriebonx98` | GitHub owner of the private data repository |
| `GAMEOS_DATA_REPO_NAME` | `Game.OS.Private.Data` | Repository name for user data |
| `GAMEOS_GITHUB_TOKEN` | *(none)* | Fine-grained PAT — developer/CI override; takes priority over `gameos-token.dat` |

> **For developers running from source:** just start the backend server and press ▶ in VS.
> The `launchSettings.json` wires up the backend URL automatically.
> End users running a published build get the URL via `gameos-backend.url` (injected at CI build time).

---

## Account Setup

The launcher shares accounts with the web frontend.  To get started:

1. **Create an account** on the [Game.OS website](../README.md) (or via the in-app register form)
2. **Launch the app** and sign in with the same username and password
3. Your game library, friends list, achievements, and profile are all synced via GitHub

The **Remember me** checkbox saves your session locally so the next launch signs you in automatically
— identical to how the website handles `localStorage` session persistence.

---

## Admin Login

Log in with the admin account (`Admin.GameOS`) to unlock admin features:

- The **Games Store** page shows an **Admin — Catalog Management** panel
- Admin can **Add** new games to the in-store catalog
- Admin can **Remove** games from the catalog

The admin account is created automatically in the GitHub data repository on first web-frontend
launch.  The default password is `GameOS2026` — change it immediately via Account Settings.

---

## How It Connects to the Backend

The launcher supports two connection modes:

### Mode 1 — Backend REST API (preferred)

When `GAMEOS_BACKEND_URL` is set, the launcher calls the **Node.js backend REST API** —
the same server the web frontend calls in backend mode.  No GitHub PAT is required:

```
Launcher  →  POST {GAMEOS_BACKEND_URL}/api/auth/token   (login)
             GET  {GAMEOS_BACKEND_URL}/api/me/games      (game library)
             GET  {GAMEOS_BACKEND_URL}/api/me/achievements (achievements)
```

### Mode 2 — GitHub API Direct (fallback)

When `GAMEOS_BACKEND_URL` is not set, the launcher calls the **GitHub REST API** directly —
the same API calls made by the web frontend's GitHub-direct mode.  Requires a GitHub PAT:

```
Launcher  →  https://api.github.com/repos/{owner}/{repo}/contents/{path}
Website   →  https://api.github.com/repos/{owner}/{repo}/contents/{path}
```

Data is stored as JSON files in the private data repository:

```
accounts/
  email-index.json          ← email → username mapping
  {username}/
    profile.json            ← credentials + metadata
    games.json              ← game library
    achievements.json       ← achievements list
    friends.json            ← friend usernames
    friend_requests.json    ← incoming requests
    presence.json           ← last-seen timestamp
  messages/
    {user1}_{user2}.json    ← message threads
```

Password hashing uses **PBKDF2-SHA256 with 100,000 iterations** — identical to the website — so
an account created in the browser works in the launcher without any migration.

---

## Build a standalone EXE

```bash
# Windows x64 — keep non-single-file (ships WebView2 + LibVLC runtime assets)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false

# macOS (Apple Silicon)
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true

# Linux x64
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

The published output appears in `bin/Release/net8.0/<rid>/publish/`.
For Windows, distribute the full publish folder (installer or portable bundle), not just `GameLauncher.exe`.

---

## Project Structure

```
GameLauncher/
├── Program.cs                  # Entry point (Avalonia bootstrap)
├── App.axaml / App.axaml.cs    # Application-level styles + startup
├── GameOsClient.cs             # Game.OS backend HTTP API client
├── DemoData.cs                 # Static store catalog + game metadata for enrichment
├── Models/Models.cs            # UserProfile, Game, Achievement, FriendEntry, …
├── Styles/
│   └── GameOsStyles.axaml      # Dark Xbox/PS5/Steam theme (colours, cards, buttons)
├── ViewModels/
│   ├── MainViewModel.cs        # Navigation state + session (MVVM root)
│   ├── LoginViewModel.cs       # Sign-in / register logic
│   ├── DashboardViewModel.cs   # Stats, recent games, featured hero
│   ├── LibraryViewModel.cs     # Filter, search, game collection
│   ├── StoreViewModel.cs       # Browse, search, genre filter, add to library, admin panel
│   ├── FriendsViewModel.cs     # Friends list loaded from API with presence status
│   └── ProfileViewModel.cs     # Avatar, stats, achievements
└── Views/
    ├── MainWindow.axaml        # Window shell with left-sidebar navigation
    ├── LoginView.axaml         # Graphical login/register screen
    ├── DashboardView.axaml     # Xbox-style home with hero banner + game tiles
    ├── LibraryView.axaml       # Game cover card grid
    ├── StoreView.axaml         # Store with featured carousel + catalogue + admin panel
    └── ProfileView.axaml       # Profile card + achievements list
```

---

## Design

- **Background**: `#0d1117` (GitHub dark / Xbox One dark)  
- **Accent**: `#1f6feb` (GitHub blue / Xbox-style blue)  
- **Success**: `#238636` (green — PS5 / Xbox add-to-library)  
- **Cards**: Rounded 10-12px, dark `#161b22` with hover lift  
- **Typography**: Inter / Segoe UI — bold headings, muted metadata
