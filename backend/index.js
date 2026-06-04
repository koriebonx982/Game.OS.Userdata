/**
 * Game.OS Userdata – Backend Server
 *
 * Stores user accounts as JSON files in a private GitHub repository.
 * Deploy this server to Railway, Render, Fly.io, or any Node.js host.
 *
 * Required environment variables (see .env.example):
 *   GITHUB_TOKEN       - Personal access token with "repo" scope
 *   REPO_OWNER         - GitHub username/org that owns the data repo
 *   REPO_NAME          - Name of the private data repository
 *   TOKEN_HMAC_SECRET  - Secret key for signing Game OS API tokens
 *   PORT               - (optional) Port to listen on, default 3000
 *   ALLOWED_ORIGIN     - (optional) Frontend URL for CORS, default allows all
 */

const express = require('express');
const { Octokit } = require('@octokit/rest');
const bcrypt = require('bcryptjs');
const crypto = require('crypto');
const cors = require('cors');
const cheerio = require('cheerio');
require('dotenv').config();

const app = express();

// ── CORS ──────────────────────────────────────────────────────────────────────
const corsOptions = {
    origin: process.env.ALLOWED_ORIGIN || '*',
    methods: ['GET', 'POST', 'PUT', 'DELETE'],
    allowedHeaders: ['Content-Type', 'Authorization']
};
app.use(cors(corsOptions));

// ── Body parser ───────────────────────────────────────────────────────────────
app.use(express.json({ limit: '10kb' }));

// ── GitHub client ─────────────────────────────────────────────────────────────
const octokit = new Octokit({ auth: process.env.GITHUB_TOKEN });
const REPO_OWNER = process.env.REPO_OWNER;
const REPO_NAME  = process.env.REPO_NAME;
const BCRYPT_ROUNDS = 10;

// ── Games.Database client (optional) ─────────────────────────────────────────
// Used to write achievements.json alongside Name.txt in Data/{folder}/Games/{titleId}/
const GAMES_DB_TOKEN      = process.env.GAMES_DB_TOKEN || null;
const GAMES_DB_REPO_OWNER = process.env.GAMES_DB_REPO_OWNER || 'Koriebonx98';
const GAMES_DB_REPO_NAME  = process.env.GAMES_DB_REPO_NAME  || 'Games.Database';
const octokitGamesDb = GAMES_DB_TOKEN ? new Octokit({ auth: GAMES_DB_TOKEN }) : null;

// ── Steam Web API key (optional) ──────────────────────────────────────────────
// Optional. When provided, it is included in calls to ISteamUserStats/GetSchemaForGame.
// When the Steam API returns no achievements (common without a key for many games),
// the scraper automatically falls back to HTML scraping of public Steam pages using
// Cheerio — the same technique used for Exophase scraping — so no API key is required.
// Generate a key at: https://steamcommunity.com/dev/apikey
const STEAM_API_KEY = process.env.STEAM_API_KEY || null;

/**
 * Map a platform name (as used in games.json) to its folder name inside
 * Data/ in the Games.Database repository.
 * Returns null for unknown platforms.
 */
const PLATFORM_TO_GAMES_DB_FOLDER = {
    'Switch':   'Nintendo - Switch',
    'Xbox 360': 'Microsoft - Xbox 360',
    'PS3':      'Sony - PlayStation 3',
    'PS4':      'Sony - PlayStation 4',
    'PS5':      'Sony - PlayStation 5',
    'Xbox One': 'Microsoft - Xbox One',
    'PC':       'PC'
};
function platformToGamesDbFolder(platform) {
    return PLATFORM_TO_GAMES_DB_FOLDER[platform] || null;
}

/**
 * Read a file from the Games.Database repository.
 * Returns { content (raw string), sha } on success, or null if not found.
 */
async function getGamesDbFile(path) {
    if (!octokitGamesDb) return null;
    try {
        const { data } = await octokitGamesDb.repos.getContent({
            owner: GAMES_DB_REPO_OWNER,
            repo:  GAMES_DB_REPO_NAME,
            path
        });
        return {
            content: Buffer.from(data.content, 'base64').toString('utf8'),
            sha: data.sha
        };
    } catch (err) {
        if (err.status === 404) return null;
        throw err;
    }
}

/**
 * Create or update a JSON file in the Games.Database repository.
 * Pass `sha` when updating an existing file.
 */
async function putGamesDbFile(path, content, message, sha) {
    if (!octokitGamesDb) throw new Error('GAMES_DB_TOKEN is not configured.');
    const params = {
        owner: GAMES_DB_REPO_OWNER,
        repo:  GAMES_DB_REPO_NAME,
        path,
        message,
        content: Buffer.from(JSON.stringify(content, null, 2)).toString('base64'),
        committer: { name: 'Game OS Bot', email: 'bot@gameos.com' }
    };
    if (sha) params.sha = sha;
    await octokitGamesDb.repos.createOrUpdateFileContents(params);
}

/**
 * Write a (potentially large) JSON file to the Games.Database repository using
 * the Git Data API (blobs → trees → commits → ref update).  This avoids the
 * 1 MB Contents-API limit that affects Switch.Games.json and PS3.Games.json.
 */
async function putGamesDbFileLarge(path, content, message) {
    if (!octokitGamesDb) throw new Error('GAMES_DB_TOKEN is not configured.');
    const owner = GAMES_DB_REPO_OWNER;
    const repo  = GAMES_DB_REPO_NAME;

    // 1. Get current branch tip SHA
    const { data: ref } = await octokitGamesDb.git.getRef({ owner, repo, ref: 'heads/main' });
    const latestSha = ref.object.sha;

    // 2. Get the commit's tree SHA
    const { data: commit } = await octokitGamesDb.git.getCommit({ owner, repo, commit_sha: latestSha });
    const treeSha = commit.tree.sha;

    // 3. Create a new blob with the updated content
    // Use compact JSON (no indentation) to minimise payload size and avoid the
    // blob API size limit that causes 422 for large files like PC.Games.json.
    const { data: blob } = await octokitGamesDb.git.createBlob({
        owner, repo,
        content: Buffer.from(JSON.stringify(content)).toString('base64'),
        encoding: 'base64'
    });

    // 4. Create a new tree that replaces only this file
    const { data: tree } = await octokitGamesDb.git.createTree({
        owner, repo,
        base_tree: treeSha,
        tree: [{ path, mode: '100644', type: 'blob', sha: blob.sha }]
    });

    // 5. Create the commit
    const { data: newCommit } = await octokitGamesDb.git.createCommit({
        owner, repo,
        message,
        tree: tree.sha,
        parents: [latestSha],
        author: { name: 'Game.OS Admin', email: ADMIN_EMAIL, date: new Date().toISOString() }
    });

    // 6. Update the branch ref
    await octokitGamesDb.git.updateRef({ owner, repo, ref: 'heads/main', sha: newCommit.sha });
    return newCommit;
}

// ── Game OS API token system ──────────────────────────────────────────────────

const TOKEN_PREFIX = 'gos_';

if (!process.env.TOKEN_HMAC_SECRET) {
    console.warn('⚠️  WARNING: TOKEN_HMAC_SECRET is not set. API token signing is insecure. Set this environment variable before going to production.');
}
const TOKEN_HMAC_SECRET = process.env.TOKEN_HMAC_SECRET || 'change-me-please';

// ── In-memory token cache ─────────────────────────────────────────────────────
// Caches recently issued tokens to bridge the GitHub API propagation delay.
// After POST /api/auth/token writes the token hash to GitHub, a subsequent
// authenticated request (e.g. GET /api/me) might still see the old profile
// from GitHub's API cache, causing a spurious 401.  This in-memory cache
// ensures the token is valid immediately after login.
//
// TTL: 5 minutes — long enough to cover any GitHub propagation lag.
// Revocation: evicted synchronously when POST /api/auth/revoke-token is called.

const _tokenCache    = new Map(); // rawToken → { usernameLower, username, email, expiresAt }
const TOKEN_CACHE_TTL_MS = 5 * 60 * 1000; // 5 minutes

/** Add a newly issued token to the in-memory cache. */
function _cacheToken(rawToken, usernameLower, username, email) {
    _tokenCache.set(rawToken, {
        usernameLower,
        username:  username  || usernameLower,
        email:     email     || '',
        expiresAt: Date.now() + TOKEN_CACHE_TTL_MS,
    });
}

/**
 * Return the cached token entry if it exists and hasn't expired.
 * Expired entries are pruned lazily on lookup.
 */
function _lookupTokenCache(rawToken) {
    const entry = _tokenCache.get(rawToken);
    if (!entry) return null;
    if (entry.expiresAt < Date.now()) {
        _tokenCache.delete(rawToken);
        return null;
    }
    return entry;
}

/** Remove all cached tokens issued to a given account (used on revoke). */
function _evictTokensForUser(usernameLower) {
    const toDelete = [];
    for (const [token, entry] of _tokenCache.entries()) {
        if (entry.usernameLower === usernameLower) {
            toDelete.push(token);
        }
    }
    for (const token of toDelete) {
        _tokenCache.delete(token);
    }
}

// Periodically remove expired entries to prevent unbounded cache growth.
setInterval(() => {
    const now = Date.now();
    const toDelete = [];
    for (const [token, entry] of _tokenCache.entries()) {
        if (entry.expiresAt < now) toDelete.push(token);
    }
    for (const token of toDelete) _tokenCache.delete(token);
}, TOKEN_CACHE_TTL_MS).unref(); // .unref() so this timer doesn't keep the process alive

// ── Public API key ────────────────────────────────────────────────────────────
// A single shared key for C# apps / game launchers that need read access
// without authenticating as a specific user.  Set PUBLIC_API_KEY in .env.
const PUBLIC_API_KEY = process.env.PUBLIC_API_KEY || null;
if (!PUBLIC_API_KEY) {
    console.warn('⚠️  WARNING: PUBLIC_API_KEY is not set. The /api/public-key endpoint will return null and public-key auth will be disabled.');
}

// Display name used for system/public-key-issued invites
const PUBLIC_INVITE_SENDER = process.env.PUBLIC_INVITE_SENDER || 'GameOS';

/** Generate a UUID-like identifier using crypto.randomBytes. */
function generateInviteId() {
    const b = crypto.randomBytes(16);
    b[6] = (b[6] & 0x0f) | 0x40; // version 4
    b[8] = (b[8] & 0x3f) | 0x80; // variant
    const h = b.toString('hex');
    return `${h.slice(0,8)}-${h.slice(8,12)}-${h.slice(12,16)}-${h.slice(16,20)}-${h.slice(20)}`;
}

/**
 * Generate a new raw API token embedding the username.
 * Format: gos_{username}.{32-byte random hex}
 */
function generateRawToken(username) {
    const randomPart = crypto.randomBytes(32).toString('hex');
    return `${TOKEN_PREFIX}${username.toLowerCase()}.${randomPart}`;
}

/**
 * Parse the username embedded in a raw token.
 * Returns null when the token format is invalid.
 */
function parseTokenUsername(rawToken) {
    if (!rawToken || !rawToken.startsWith(TOKEN_PREFIX)) return null;
    const rest     = rawToken.slice(TOKEN_PREFIX.length);
    // Use lastIndexOf so usernames that contain dots (e.g. Admin.GameOS) parse correctly.
    // The random hex suffix never contains dots, so the last dot is always the separator.
    const dotIndex = rest.lastIndexOf('.');
    if (dotIndex === -1) return null;
    return rest.slice(0, dotIndex);
}

/**
 * HMAC-SHA256 hash of a raw token.  This is what gets stored in the data repo.
 */
function hashToken(rawToken) {
    return crypto.createHmac('sha256', TOKEN_HMAC_SECRET).update(rawToken).digest('hex');
}

/**
 * SHA-256 hash of a raw token (no key).
 * Used when a token was generated by the frontend in GitHub mode (no HMAC secret available).
 */
function sha256Token(rawToken) {
    return crypto.createHash('sha256').update(rawToken).digest('hex');
}

/**
 * Verify a password that was hashed by the frontend using PBKDF2.
 * Salt = "{username_lower}:gameos", 100000 iterations, SHA-256, 32 bytes.
 */
function verifyPbkdf2Password(password, username) {
    return new Promise((resolve, reject) => {
        const salt = `${username.toLowerCase()}:gameos`;
        crypto.pbkdf2(password, salt, 100000, 32, 'sha256', (err, key) => {
            if (err) return reject(err);
            resolve(key.toString('hex'));
        });
    });
}

/** Returns true when the stored hash was produced by bcrypt (starts with "$2"). */
function isBcryptHash(hash) {
    return typeof hash === 'string' && hash.startsWith('$2');
}

/**
 * Verify a password against a stored hash that may be bcrypt (backend-created)
 * or PBKDF2 hex (frontend-created in GitHub mode).
 */
async function verifyPassword(password, storedHash, username) {
    if (isBcryptHash(storedHash)) {
        return bcrypt.compare(password, storedHash);
    }
    const pbkdf2Hash = await verifyPbkdf2Password(password, username);
    return pbkdf2Hash === storedHash;
}

// ── Admin account constants ───────────────────────────────────────────────────
const ADMIN_USERNAME       = 'Admin.GameOS';
const ADMIN_USERNAME_LOWER = ADMIN_USERNAME.toLowerCase(); // 'admin.gameos'
const ADMIN_EMAIL          = 'admin@gameos.local';

// ── Simple in-memory rate limiter ─────────────────────────────────────────────

const _rateLimitMap = new Map(); // ip -> { count, resetAt }
const RATE_WINDOW_MS = 60 * 1000; // 1 minute window
const RATE_LIMIT_AUTH = 10;       // token-generation attempts per minute per IP
const RATE_LIMIT_API  = 120;      // authenticated API calls per minute per IP

function checkRateLimit(ip, limit) {
    const now   = Date.now();
    const entry = _rateLimitMap.get(ip);
    if (!entry || now > entry.resetAt) {
        _rateLimitMap.set(ip, { count: 1, resetAt: now + RATE_WINDOW_MS });
        return true;
    }
    if (entry.count >= limit) return false;
    entry.count++;
    return true;
}

// Purge stale rate-limit entries every 5 minutes
setInterval(() => {
    const now = Date.now();
    for (const [ip, entry] of _rateLimitMap.entries()) {
        if (now > entry.resetAt) _rateLimitMap.delete(ip);
    }
}, 5 * 60 * 1000);

// ── Token authentication middleware ──────────────────────────────────────────

async function authenticateToken(req, res, next) {
    const ip = req.ip || (req.connection && req.connection.remoteAddress) || 'unknown';
    if (!checkRateLimit(ip, RATE_LIMIT_API)) {
        return res.status(429).json({ success: false, message: 'Too many requests – please slow down.' });
    }

    const authHeader = req.headers['authorization'];
    const rawToken   = (authHeader && authHeader.startsWith('Bearer '))
        ? authHeader.slice(7).trim()
        : null;

    if (!rawToken) {
        return res.status(401).json({
            success: false,
            message: 'Missing API token. Add an Authorization: Bearer gos_... header.'
        });
    }

    const usernameLower = parseTokenUsername(rawToken);
    if (!usernameLower || !sanitiseUsername(usernameLower)) {
        return res.status(401).json({ success: false, message: 'Invalid API token format.' });
    }

    // Check the in-memory cache first — covers the window between a token being
    // issued (POST /api/auth/token) and GitHub's API returning the updated profile.
    const cached = _lookupTokenCache(rawToken);
    if (cached) {
        req.tokenUser = {
            username:      cached.username,
            email:         cached.email,
            usernameLower: cached.usernameLower,
        };
        return next();
    }

    try {
        const accountFile = await getFile(`accounts/${usernameLower}/profile.json`);
        const storedHash  = accountFile && accountFile.content.api_token_hash;
        // Accept tokens hashed with HMAC-SHA256 (backend-issued) or plain SHA-256 (frontend-issued)
        if (!accountFile || (storedHash !== hashToken(rawToken) && storedHash !== sha256Token(rawToken))) {
            return res.status(401).json({ success: false, message: 'Invalid or revoked API token.' });
        }
        req.tokenUser = {
            username:      accountFile.content.username,
            email:         accountFile.content.email,
            usernameLower
        };
        next();
    } catch (err) {
        console.error('Token auth error:', err);
        res.status(500).json({ success: false, message: 'Server error during token validation.' });
    }
}

/**
 * Middleware that accepts EITHER a per-user token OR the shared PUBLIC_API_KEY.
 * When the public key is used, req.tokenUser is set to { publicKeyAuth: true }
 * and the route is responsible for reading the target username from the query/body.
 * When a per-user token is used the behaviour is identical to authenticateToken.
 */
async function authenticatePublicOrUserToken(req, res, next) {
    const ip = req.ip || (req.connection && req.connection.remoteAddress) || 'unknown';
    if (!checkRateLimit(ip, RATE_LIMIT_API)) {
        return res.status(429).json({ success: false, message: 'Too many requests – please slow down.' });
    }

    const authHeader = req.headers['authorization'];
    const rawToken   = (authHeader && authHeader.startsWith('Bearer '))
        ? authHeader.slice(7).trim()
        : null;

    if (!rawToken) {
        return res.status(401).json({
            success: false,
            message: 'Missing API token. Add an Authorization: Bearer <token> header.'
        });
    }

    // Accept the shared public key
    if (PUBLIC_API_KEY && rawToken === PUBLIC_API_KEY) {
        req.tokenUser = { publicKeyAuth: true };
        return next();
    }

    // Fall back to per-user token validation
    const usernameLower = parseTokenUsername(rawToken);
    if (!usernameLower || !sanitiseUsername(usernameLower)) {
        return res.status(401).json({ success: false, message: 'Invalid API token format.' });
    }

    // Check the in-memory cache first
    const cached = _lookupTokenCache(rawToken);
    if (cached) {
        req.tokenUser = {
            username:      cached.username,
            email:         cached.email,
            usernameLower: cached.usernameLower,
        };
        return next();
    }

    try {
        const accountFile = await getFile(`accounts/${usernameLower}/profile.json`);
        const storedHash  = accountFile && accountFile.content.api_token_hash;
        // Accept tokens hashed with HMAC-SHA256 (backend-issued) or plain SHA-256 (frontend-issued)
        if (!accountFile || (storedHash !== hashToken(rawToken) && storedHash !== sha256Token(rawToken))) {
            return res.status(401).json({ success: false, message: 'Invalid or revoked API token.' });
        }
        req.tokenUser = {
            username:      accountFile.content.username,
            email:         accountFile.content.email,
            usernameLower
        };
        next();
    } catch (err) {
        console.error('Token auth error:', err);
        res.status(500).json({ success: false, message: 'Server error during token validation.' });
    }
}

// ── GitHub helpers ────────────────────────────────────────────────────────────

/**
 * Read a JSON file from the data repository.
 * Returns { content, sha } on success, or null if the file doesn't exist.
 */
async function getFile(path) {
    try {
        const { data } = await octokit.repos.getContent({
            owner: REPO_OWNER,
            repo:  REPO_NAME,
            path
        });
        const content = Buffer.from(data.content, 'base64').toString('utf8');
        return { content: JSON.parse(content), sha: data.sha };
    } catch (err) {
        if (err.status === 404) return null;
        throw err;
    }
}

/**
 * Create or update a JSON file in the data repository.
 * Pass `sha` when updating an existing file.
 */
async function putFile(path, content, message, sha) {
    const params = {
        owner: REPO_OWNER,
        repo:  REPO_NAME,
        path,
        message,
        content: Buffer.from(JSON.stringify(content, null, 2)).toString('base64'),
        committer: {
            name:  'Game OS Bot',
            email: 'bot@gameos.com'
        }
    };
    if (sha) params.sha = sha;
    await octokit.repos.createOrUpdateFileContents(params);
}

const STEAM_ID_INDEX_PATH = 'accounts/SteamID.json';

function normaliseSteamUserId(value) {
    const trimmed = String(value || '').trim();
    return /^\d{5,32}$/.test(trimmed) ? trimmed : '';
}

async function writeProfileWithSteamBinding(usernameLower, profile, profileSha, requestedSteamUserId, messageBase) {
    const normalisedSteamId = normaliseSteamUserId(requestedSteamUserId);
    const previousSteamId   = normaliseSteamUserId(profile.steamUserId || '');
    const requestedSteamUserIdTrimmed = String(requestedSteamUserId || '').trim();
    const steamIndexFile    = await getFile(STEAM_ID_INDEX_PATH);
    const steamIndex        = steamIndexFile && steamIndexFile.content && typeof steamIndexFile.content === 'object'
        ? { ...steamIndexFile.content }
        : {};

    if (requestedSteamUserId !== undefined && !normalisedSteamId && requestedSteamUserIdTrimmed) {
        const err = new Error('Steam ID must be a numeric SteamID64.');
        err.statusCode = 400;
        throw err;
    }

    if (normalisedSteamId) {
        const existingOwnerLower = typeof steamIndex[normalisedSteamId] === 'string'
            ? steamIndex[normalisedSteamId].toLowerCase()
            : '';
        if (existingOwnerLower && existingOwnerLower !== usernameLower) {
            let ownerName = steamIndex[normalisedSteamId];
            try {
                const ownerProfile = await getFile(`accounts/${existingOwnerLower}/profile.json`);
                if (ownerProfile?.content?.username)
                    ownerName = ownerProfile.content.username;
            } catch { }
            const err = new Error(`This Steam ID is already linked to ${ownerName}.`);
            err.statusCode = 409;
            throw err;
        }
    }

    if (previousSteamId &&
        typeof steamIndex[previousSteamId] === 'string' &&
        steamIndex[previousSteamId].toLowerCase() === usernameLower &&
        previousSteamId !== normalisedSteamId) {
        delete steamIndex[previousSteamId];
    }

    if (normalisedSteamId)
        steamIndex[normalisedSteamId] = usernameLower;

    profile.steamUserId = normalisedSteamId || '';

    await putFile(`accounts/${usernameLower}/profile.json`, profile,
        `${messageBase}: ${usernameLower}`, profileSha);
    await putFile(STEAM_ID_INDEX_PATH, steamIndex,
        `${messageBase} steam index: ${usernameLower}`, steamIndexFile ? steamIndexFile.sha : undefined);

    return profile;
}

// ── Validation helpers ────────────────────────────────────────────────────────

function isValidEmail(email) {
    return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email);
}

function sanitiseUsername(username) {
    // Allow alphanumeric, underscore, hyphen, and dot (supports usernames like Admin.GameOS)
    return /^[a-zA-Z0-9._-]+$/.test(username);
}

function sanitisePathSegment(value, fallback = 'unknown') {
    const raw = String(value || '').trim();
    const cleaned = raw.replace(/[^a-zA-Z0-9._-]/g, '_').slice(0, 80);
    return cleaned || fallback;
}

function normalizePlatformName(platform) {
    const value = String(platform || '').trim().toLowerCase();
    switch (value) {
        case 'microsoft - xbox 360':
        case 'xbox360':
        case 'xbox 360':
            return 'Xbox 360';
        case 'nintendo - switch':
        case 'switch':
            return 'Switch';
        case 'sony - playstation 3':
        case 'ps3':
            return 'PS3';
        case 'sony - playstation 4':
        case 'ps4':
            return 'PS4';
        case 'sony - playstation 5':
        case 'ps5':
            return 'PS5';
        case 'microsoft - xbox one':
        case 'xbox one':
        case 'xbone':
            return 'Xbox One';
        case 'pc':
            return 'PC';
        default:
            return String(platform || '').trim();
    }
}

async function resolveAchievementTitleKey(usernameLower, platform, gameTitle, titleId) {
    const rawTitleId = String(titleId || '').trim();
    if (/^[a-zA-Z0-9_-]+$/.test(rawTitleId)) return rawTitleId;

    try {
        const gamesFile = await getFile(`accounts/${usernameLower}/games.json`);
        const games = Array.isArray(gamesFile?.content) ? gamesFile.content : [];
        const normalizedPlatform = normalizePlatformName(platform);
        const match = games.find(g =>
            normalizePlatformName(g.platform) === normalizedPlatform &&
            String(g.title || '').toLowerCase() === String(gameTitle || '').toLowerCase() &&
            /^[a-zA-Z0-9_-]+$/.test(String(g.titleId || '').trim()));
        if (match) return String(match.titleId).trim();
    } catch (err) {
        console.warn('resolveAchievementTitleKey lookup failed:', err.message);
    }

    return sanitisePathSegment(gameTitle, 'unknown-title');
}

// ── Routes ────────────────────────────────────────────────────────────────────

// Health check – the frontend polls this to detect the backend
app.get('/health', (req, res) => {
    res.json({ status: 'ok', message: 'Game.OS backend running' });
});

// ── GET /api/public-key ───────────────────────────────────────────────────────
// Returns the shared public API key configured via the PUBLIC_API_KEY env var.
// C# apps can call this endpoint once to retrieve the key (no auth required),
// or operators can copy it from the server environment directly.
app.get('/api/public-key', (req, res) => {
    res.json({
        success:    true,
        publicKey:  PUBLIC_API_KEY || null,
        configured: PUBLIC_API_KEY !== null
    });
});

// ── GET /api/users-count ──────────────────────────────────────────────────────
app.get('/api/users-count', async (req, res) => {
    try {
        const emailIndexFile = await getFile('accounts/email-index.json');
        const count = emailIndexFile ? Object.keys(emailIndexFile.content).length : 0;
        res.json({ success: true, count });
    } catch (err) {
        console.error('Error getting user count:', err);
        res.status(500).json({ success: false, message: 'Failed to get user count.' });
    }
});

// ── GET /api/auth/token-status ────────────────────────────────────────────────
// Returns whether the user currently has an active API token (no sensitive data).
app.get('/api/auth/token-status', async (req, res) => {
    try {
        const { username } = req.query;
        if (!username || !sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username.' });
        }
        const accountFile = await getFile(`accounts/${username.toLowerCase()}/profile.json`);
        const hasToken = !!(accountFile && accountFile.content.api_token_hash);
        res.json({ success: true, hasToken });
    } catch (err) {
        console.error('GET /api/auth/token-status error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/game-library ─────────────────────────────────────────────────────
// Read any user's game library without authentication (public data).
app.get('/api/game-library', async (req, res) => {
    try {
        const { username } = req.query;
        if (!username || !sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username.' });
        }
        const file = await getFile(`accounts/${username.toLowerCase()}/games.json`);
        res.json({ success: true, games: file ? file.content : [] });
    } catch (err) {
        console.error('GET /api/game-library error:', err);
        res.status(500).json({ success: false, message: 'Failed to get game library.' });
    }
});

// ── POST /api/game-library/add ────────────────────────────────────────────────
// Add a game to a user's library.
// Security: the caller must supply the account password to prove ownership.
app.post('/api/game-library/add', async (req, res) => {
    const ip = req.ip || (req.connection && req.connection.remoteAddress) || 'unknown';
    if (!checkRateLimit(ip, RATE_LIMIT_AUTH)) {
        return res.status(429).json({ success: false, message: 'Too many requests – wait a minute and try again.' });
    }

    try {
        const { username, password, platform, title, titleId, coverUrl,
                mods, sysSpecMin, sysSpecRecommended, achievementsUrl } = req.body;

        if (!username || !password || !platform || !title) {
            return res.status(400).json({ success: false, message: 'username, password, platform, and title are required.' });
        }
        if (!sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username.' });
        }

        const usernameLower = username.toLowerCase();
        const accountFile   = await getFile(`accounts/${usernameLower}/profile.json`);
        if (!accountFile) {
            return res.status(404).json({ success: false, message: 'Account not found.' });
        }

        // Verify password to prevent unauthorised writes
        const valid = await verifyPassword(password, accountFile.content.password_hash, accountFile.content.username);
        if (!valid) {
            return res.status(401).json({ success: false, message: 'Invalid password.' });
        }

        const path    = `accounts/${usernameLower}/games.json`;
        const file    = await getFile(path);
        const library = file ? [...file.content] : [];

        const alreadyOwned = library.some(
            g => g.platform === platform && (g.title || '').toLowerCase() === title.toLowerCase()
        );
        if (alreadyOwned) {
            return res.status(400).json({ success: false, message: 'Game already in library.' });
        }

        const entry = {
            platform,
            title,
            titleId:  titleId || null,
            coverUrl: coverUrl || undefined,
            addedAt:  new Date().toISOString()
        };

        // Validate and sanitise optional structured fields
        if (Array.isArray(mods) && mods.length) {
            const cleanMods = mods.filter(m => m && typeof m.name === 'string' && typeof m.url === 'string' && m.name.trim() && m.url.trim())
                                  .map(m => ({ name: m.name.trim(), url: m.url.trim() }));
            if (cleanMods.length) entry.mods = cleanMods;
        }
        const SPEC_KEYS = ['cpu', 'gpu', 'ram', 'resolution'];
        if (sysSpecMin && typeof sysSpecMin === 'object' && !Array.isArray(sysSpecMin)) {
            const clean = {};
            SPEC_KEYS.forEach(k => { if (sysSpecMin[k]) clean[k] = String(sysSpecMin[k]).trim(); });
            if (Object.keys(clean).length) entry.sysSpecMin = clean;
        }
        if (sysSpecRecommended && typeof sysSpecRecommended === 'object' && !Array.isArray(sysSpecRecommended)) {
            const clean = {};
            SPEC_KEYS.forEach(k => { if (sysSpecRecommended[k]) clean[k] = String(sysSpecRecommended[k]).trim(); });
            if (Object.keys(clean).length) entry.sysSpecRecommended = clean;
        }
        if (achievementsUrl && typeof achievementsUrl === 'string') entry.achievementsUrl = achievementsUrl.trim();

        library.push(entry);

        await putFile(path, library, `Add game: ${title} (${platform})`, file ? file.sha : undefined);
        res.json({ success: true, message: 'Game added to your library!' });
    } catch (err) {
        console.error('POST /api/game-library/add error:', err);
        res.status(500).json({ success: false, message: 'Failed to add game.' });
    }
});

// ── POST /api/game-library/remove ─────────────────────────────────────────────
// Remove a game from a user's library.
// Security: the caller must supply the account password to prove ownership.
app.post('/api/game-library/remove', async (req, res) => {
    const ip = req.ip || (req.connection && req.connection.remoteAddress) || 'unknown';
    if (!checkRateLimit(ip, RATE_LIMIT_AUTH)) {
        return res.status(429).json({ success: false, message: 'Too many requests – wait a minute and try again.' });
    }

    try {
        const { username, password, platform, title } = req.body;

        if (!username || !password || !platform || !title) {
            return res.status(400).json({ success: false, message: 'username, password, platform, and title are required.' });
        }
        if (!sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username.' });
        }

        const usernameLower = username.toLowerCase();
        const accountFile   = await getFile(`accounts/${usernameLower}/profile.json`);
        if (!accountFile) {
            return res.status(404).json({ success: false, message: 'Account not found.' });
        }

        // Verify password to prevent unauthorised deletions
        const valid = await verifyPassword(password, accountFile.content.password_hash, accountFile.content.username);
        if (!valid) {
            return res.status(401).json({ success: false, message: 'Invalid password.' });
        }

        const path = `accounts/${usernameLower}/games.json`;
        const file = await getFile(path);
        if (!file) return res.status(404).json({ success: false, message: 'Game library not found.' });

        const updated = file.content.filter(
            g => !(g.platform === platform && (g.title || '').toLowerCase() === title.toLowerCase())
        );
        await putFile(path, updated, `Remove game: ${title} (${platform})`, file.sha);
        res.json({ success: true, message: 'Game removed from library.', library: updated });
    } catch (err) {
        console.error('POST /api/game-library/remove error:', err);
        res.status(500).json({ success: false, message: 'Failed to remove game.' });
    }
});

// ── GET /api/wishlist ─────────────────────────────────────────────────────────
// Read any user's wishlist without authentication (public data).
app.get('/api/wishlist', async (req, res) => {
    try {
        const { username } = req.query;
        if (!username || !sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username.' });
        }
        const file = await getFile(`accounts/${username.toLowerCase()}/wishlist.json`);
        res.json({ success: true, wishlist: file ? file.content : [] });
    } catch (err) {
        console.error('GET /api/wishlist error:', err);
        res.status(500).json({ success: false, message: 'Failed to get wishlist.' });
    }
});

// ── POST /api/wishlist/add ────────────────────────────────────────────────────
// Add a game to a user's wishlist.
// Security: the caller must supply the account password to prove ownership.
app.post('/api/wishlist/add', async (req, res) => {
    const ip = req.ip || (req.connection && req.connection.remoteAddress) || 'unknown';
    if (!checkRateLimit(ip, RATE_LIMIT_AUTH)) {
        return res.status(429).json({ success: false, message: 'Too many requests – wait a minute and try again.' });
    }

    try {
        const { username, password, platform, title, titleId, coverUrl } = req.body;

        if (!username || !password || !platform || !title) {
            return res.status(400).json({ success: false, message: 'username, password, platform, and title are required.' });
        }
        if (!sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username.' });
        }

        const usernameLower = username.toLowerCase();
        const accountFile   = await getFile(`accounts/${usernameLower}/profile.json`);
        if (!accountFile) {
            return res.status(404).json({ success: false, message: 'Account not found.' });
        }

        const valid = await verifyPassword(password, accountFile.content.password_hash, accountFile.content.username);
        if (!valid) {
            return res.status(401).json({ success: false, message: 'Invalid password.' });
        }

        const path     = `accounts/${usernameLower}/wishlist.json`;
        const file     = await getFile(path);
        const wishlist = file ? [...file.content] : [];

        const alreadyWishlisted = wishlist.some(
            g => g.platform === platform && (g.title || '').toLowerCase() === title.toLowerCase()
        );
        if (alreadyWishlisted) {
            return res.status(400).json({ success: false, message: 'Game already in wishlist.' });
        }

        wishlist.push({
            platform,
            title,
            titleId:  titleId || null,
            coverUrl: coverUrl || undefined,
            addedAt:  new Date().toISOString()
        });

        await putFile(path, wishlist, `Add to wishlist: ${title} (${platform})`, file ? file.sha : undefined);
        res.json({ success: true, message: 'Game added to your wishlist!' });
    } catch (err) {
        console.error('POST /api/wishlist/add error:', err);
        res.status(500).json({ success: false, message: 'Failed to add game to wishlist.' });
    }
});

// ── POST /api/wishlist/remove ─────────────────────────────────────────────────
// Remove a game from a user's wishlist.
// Security: the caller must supply the account password to prove ownership.
app.post('/api/wishlist/remove', async (req, res) => {
    const ip = req.ip || (req.connection && req.connection.remoteAddress) || 'unknown';
    if (!checkRateLimit(ip, RATE_LIMIT_AUTH)) {
        return res.status(429).json({ success: false, message: 'Too many requests – wait a minute and try again.' });
    }

    try {
        const { username, password, platform, title } = req.body;

        if (!username || !password || !platform || !title) {
            return res.status(400).json({ success: false, message: 'username, password, platform, and title are required.' });
        }
        if (!sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username.' });
        }

        const usernameLower = username.toLowerCase();
        const accountFile   = await getFile(`accounts/${usernameLower}/profile.json`);
        if (!accountFile) {
            return res.status(404).json({ success: false, message: 'Account not found.' });
        }

        const valid = await verifyPassword(password, accountFile.content.password_hash, accountFile.content.username);
        if (!valid) {
            return res.status(401).json({ success: false, message: 'Invalid password.' });
        }

        const path = `accounts/${usernameLower}/wishlist.json`;
        const file = await getFile(path);
        if (!file) return res.json({ success: true, message: 'Game removed from wishlist.', wishlist: [] });

        const updated = file.content.filter(
            g => !(g.platform === platform && (g.title || '').toLowerCase() === title.toLowerCase())
        );
        await putFile(path, updated, `Remove from wishlist: ${title} (${platform})`, file.sha);
        res.json({ success: true, message: 'Game removed from wishlist.', wishlist: updated });
    } catch (err) {
        console.error('POST /api/wishlist/remove error:', err);
        res.status(500).json({ success: false, message: 'Failed to remove game from wishlist.' });
    }
});

// ── POST /api/create-account ──────────────────────────────────────────────────
app.post('/api/create-account', async (req, res) => {
    try {
        const { username, email, password } = req.body;

        // Input validation
        if (!username || !email || !password) {
            return res.status(400).json({ success: false, message: 'All fields are required' });
        }
        if (username.length < 3) {
            return res.status(400).json({ success: false, message: 'Username must be at least 3 characters' });
        }
        if (!sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Username may only contain letters, numbers, underscores, and hyphens' });
        }
        if (!isValidEmail(email)) {
            return res.status(400).json({ success: false, message: 'Please enter a valid email address' });
        }
        if (password.length < 6) {
            return res.status(400).json({ success: false, message: 'Password must be at least 6 characters' });
        }

        // Check for duplicate username
        const existingAccount = await getFile(`accounts/${username.toLowerCase()}/profile.json`);
        if (existingAccount) {
            return res.status(400).json({ success: false, message: 'Username already exists' });
        }

        // Check for duplicate email via index
        const emailIndexFile = await getFile('accounts/email-index.json');
        const emailMap = emailIndexFile ? emailIndexFile.content : {};
        if (emailMap[email.toLowerCase()]) {
            return res.status(400).json({ success: false, message: 'Email already registered' });
        }

        // Hash the password server-side with bcrypt
        const passwordHash = await bcrypt.hash(password, BCRYPT_ROUNDS);

        // Generate an API token for the new account right away
        const rawToken  = generateRawToken(username.toLowerCase());
        const tokenHash = hashToken(rawToken);
        const createdAt = new Date().toISOString();
        const issuedAt  = new Date().toISOString();

        // Write account profile file (one folder per user)
        const accountData = {
            username,
            email,
            password_hash:       passwordHash,
            created_at:          createdAt,
            api_token_hash:      tokenHash,
            api_token_issued_at: issuedAt
        };
        await putFile(
            `accounts/${username.toLowerCase()}/profile.json`,
            accountData,
            `Create account: ${username}`
        );

        // Update email → username index (retry once on SHA conflict)
        emailMap[email.toLowerCase()] = username.toLowerCase();
        try {
            await putFile(
                'accounts/email-index.json',
                emailMap,
                `Add email index for: ${username}`,
                emailIndexFile ? emailIndexFile.sha : undefined
            );
        } catch (indexErr) {
            // If another registration raced us, re-fetch and retry
            if (indexErr.status === 409 || indexErr.status === 422) {
                const freshIndex = await getFile('accounts/email-index.json');
                const freshMap = freshIndex ? freshIndex.content : {};
                freshMap[email.toLowerCase()] = username.toLowerCase();
                await putFile(
                    'accounts/email-index.json',
                    freshMap,
                    `Add email index for: ${username}`,
                    freshIndex ? freshIndex.sha : undefined
                );
            } else {
                throw indexErr;
            }
        }

        res.json({
            success:  true,
            message:  'Account created successfully',
            token:    rawToken,
            username,
            issuedAt
        });
    } catch (err) {
        console.error('Error creating account:', err);
        res.status(500).json({ success: false, message: 'Failed to create account. Please try again.' });
    }
});

// ── POST /api/verify-account ──────────────────────────────────────────────────
app.post('/api/verify-account', async (req, res) => {
    const ip = req.ip || (req.connection && req.connection.remoteAddress) || 'unknown';
    if (!checkRateLimit(ip, RATE_LIMIT_AUTH)) {
        return res.status(429).json({ success: false, message: 'Too many requests – wait a minute and try again.' });
    }

    try {
        const { username: identifier, password } = req.body;

        if (!identifier || !password) {
            return res.status(400).json({ success: false, message: 'Missing credentials' });
        }

        // Resolve identifier (email or username) to the account file key
        let accountKey;
        if (identifier.includes('@')) {
            const emailIndexFile = await getFile('accounts/email-index.json');
            if (!emailIndexFile) {
                return res.status(401).json({ success: false, message: 'Account not found' });
            }
            accountKey = emailIndexFile.content[identifier.toLowerCase()];
            if (!accountKey) {
                return res.status(401).json({ success: false, message: 'Account not found' });
            }
        } else {
            accountKey = identifier.toLowerCase();
        }

        // Fetch account data
        const accountFile = await getFile(`accounts/${accountKey}/profile.json`);
        if (!accountFile) {
            return res.status(401).json({ success: false, message: 'Account not found' });
        }

        // Verify password
        const valid = await verifyPassword(password, accountFile.content.password_hash, accountFile.content.username);
        if (!valid) {
            return res.status(401).json({ success: false, message: 'Invalid password' });
        }

        res.json({
            success: true,
            message: 'Login successful',
            user: {
                username: accountFile.content.username,
                email:    accountFile.content.email
            }
        });
    } catch (err) {
        console.error('Error verifying account:', err);
        res.status(500).json({ success: false, message: 'Server error. Please try again.' });
    }
});

// ── POST /api/update-account ──────────────────────────────────────────────────
app.post('/api/update-account', async (req, res) => {
    const ip = req.ip || (req.connection && req.connection.remoteAddress) || 'unknown';
    if (!checkRateLimit(ip, RATE_LIMIT_AUTH)) {
        return res.status(429).json({ success: false, message: 'Too many requests – wait a minute and try again.' });
    }

    try {
        const { username, currentPassword, newEmail, newPassword } = req.body;

        if (!username || !currentPassword) {
            return res.status(400).json({ success: false, message: 'Missing required fields' });
        }

        const accountFile = await getFile(`accounts/${username.toLowerCase()}/profile.json`);
        if (!accountFile) {
            return res.status(404).json({ success: false, message: 'Account not found' });
        }

        const account = accountFile.content;
        // Support both bcrypt (backend-created) and PBKDF2 (frontend-created) password hashes
        const validUpdate = await verifyPassword(currentPassword, account.password_hash, account.username);
        if (!validUpdate) {
            return res.status(401).json({ success: false, message: 'Current password is incorrect' });
        }

        let updated = { ...account };

        if (newEmail && newEmail.toLowerCase() !== account.email.toLowerCase()) {
            if (!isValidEmail(newEmail)) {
                return res.status(400).json({ success: false, message: 'Invalid email address' });
            }
            const emailLower = newEmail.toLowerCase();
            const indexFile  = await getFile('accounts/email-index.json');
            const emailMap   = indexFile ? { ...indexFile.content } : {};
            if (emailMap[emailLower] && emailMap[emailLower] !== username.toLowerCase()) {
                return res.status(400).json({ success: false, message: 'Email already in use' });
            }
            delete emailMap[account.email.toLowerCase()];
            emailMap[emailLower] = username.toLowerCase();
            await putFile(
                'accounts/email-index.json',
                emailMap,
                `Update email index for: ${username}`,
                indexFile ? indexFile.sha : undefined
            );
            updated.email = newEmail;
        }

        if (newPassword) {
            if (newPassword.length < 6) {
                return res.status(400).json({ success: false, message: 'Password must be at least 6 characters' });
            }
            updated.password_hash = await bcrypt.hash(newPassword, BCRYPT_ROUNDS);
        }

        await putFile(
            `accounts/${username.toLowerCase()}/profile.json`,
            updated,
            `Update account: ${username}`,
            accountFile.sha
        );

        res.json({ success: true, message: 'Account updated successfully', email: updated.email });
    } catch (err) {
        console.error('Error updating account:', err);
        res.status(500).json({ success: false, message: 'Failed to update account. Please try again.' });
    }
});

// ── GET /api/check-user ───────────────────────────────────────────────────────
app.get('/api/check-user', async (req, res) => {
    try {
        const { username } = req.query;
        if (!username || !sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username' });
        }
        const accountFile = await getFile(`accounts/${username.toLowerCase()}/profile.json`);
        if (!accountFile) {
            return res.json({ exists: false });
        }
        res.json({ exists: true, username: accountFile.content.username });
    } catch (err) {
        console.error('Error checking user:', err);
        res.status(500).json({ success: false, message: 'Server error' });
    }
});

// ── POST /api/update-presence ─────────────────────────────────────────────────
app.post('/api/update-presence', async (req, res) => {
    try {
        const { username } = req.body;
        if (!username || !sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username' });
        }
        const path = `accounts/${username.toLowerCase()}/presence.json`;
        const existing = await getFile(path);
        // If the caller explicitly includes 'currentGame' in the body (even as null/empty),
        // use that value — null/empty means the user cleared the game (back at Dashboard).
        // If the caller omits 'currentGame' entirely (old web clients), preserve the stored value.
        const existingCurrentGame = existing ? (existing.content.currentGame || null) : null;
        const currentGame = Object.prototype.hasOwnProperty.call(req.body, 'currentGame')
            ? (req.body.currentGame || null)
            : existingCurrentGame;
        const data = { lastSeen: new Date().toISOString(), username, currentGame };
        await putFile(path, data, `Presence: ${username}`, existing ? existing.sha : undefined);
        res.json({ success: true });
    } catch (err) {
        console.error('Error updating presence:', err);
        res.status(500).json({ success: false, message: 'Failed to update presence.' });
    }
});

// ── GET /api/get-presence ─────────────────────────────────────────────────────
app.get('/api/get-presence', async (req, res) => {
    try {
        const { username } = req.query;
        if (!username || !sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username' });
        }
        const file = await getFile(`accounts/${username.toLowerCase()}/presence.json`);
        res.json({
            success:     true,
            lastSeen:    file ? file.content.lastSeen    : null,
            currentGame: file ? (file.content.currentGame || null) : null,
        });
    } catch (err) {
        console.error('Error getting presence:', err);
        res.status(500).json({ success: false, message: 'Failed to get presence.' });
    }
});

// ── POST /api/send-friend-request (also handles legacy /api/add-friend) ───────
async function handleSendFriendRequest(req, res) {
    try {
        const { username, friendUsername } = req.body;

        if (!username || !friendUsername) {
            return res.status(400).json({ success: false, message: 'Missing required fields' });
        }
        if (username.toLowerCase() === friendUsername.toLowerCase()) {
            return res.status(400).json({ success: false, message: 'You cannot add yourself as a friend' });
        }

        // Verify both users exist
        const userFile   = await getFile(`accounts/${username.toLowerCase()}/profile.json`);
        if (!userFile) {
            return res.status(404).json({ success: false, message: 'Your account was not found' });
        }
        const friendFile = await getFile(`accounts/${friendUsername.toLowerCase()}/profile.json`);
        if (!friendFile) {
            return res.status(404).json({ success: false, message: 'User not found' });
        }

        const userLower   = username.toLowerCase();
        const friendLower = friendUsername.toLowerCase();

        // Check if already accepted friends
        const friendsFile = await getFile(`accounts/${userLower}/friends.json`);
        const friends     = friendsFile ? friendsFile.content : [];
        if (friends.some(f => f.toLowerCase() === friendLower)) {
            return res.status(400).json({ success: false, message: 'Already friends with this user' });
        }

        // Check if they already sent us a request → auto-accept
        const myRequestsFile = await getFile(`accounts/${userLower}/friend_requests.json`);
        const myRequests     = myRequestsFile ? myRequestsFile.content : [];
        if (myRequests.some(r => r.from.toLowerCase() === friendLower)) {
            const result = await acceptFriendRequest(userLower, friendLower);
            if (!result.success) return res.status(400).json(result);
            return res.json({ success: true, message: `You are now friends with ${friendFile.content.username}!` });
        }

        // Check if we already sent them a request
        const sentFile = await getFile(`accounts/${userLower}/sent_requests.json`);
        const sent     = sentFile ? sentFile.content : [];
        if (sent.some(s => s.toLowerCase() === friendLower)) {
            return res.status(400).json({ success: false, message: 'Friend request already pending' });
        }

        // Add to recipient's incoming requests
        const theirRequestsPath = `accounts/${friendLower}/friend_requests.json`;
        const theirRequestsFile = await getFile(theirRequestsPath);
        const theirRequests     = theirRequestsFile ? [...theirRequestsFile.content] : [];
        theirRequests.push({ from: userFile.content.username, sentAt: new Date().toISOString() });
        await putFile(
            theirRequestsPath,
            theirRequests,
            `Friend request from ${username} to ${friendUsername}`,
            theirRequestsFile ? theirRequestsFile.sha : undefined
        );

        // Add to sender's outgoing requests
        sent.push(friendFile.content.username);
        await putFile(
            `accounts/${userLower}/sent_requests.json`,
            sent,
            `Sent friend request to ${friendUsername} for: ${username}`,
            sentFile ? sentFile.sha : undefined
        );

        res.json({ success: true, message: `Friend request sent to ${friendFile.content.username}! Waiting for them to accept.` });
    } catch (err) {
        console.error('Error sending friend request:', err);
        res.status(500).json({ success: false, message: 'Failed to send friend request. Please try again.' });
    }
}

app.post('/api/send-friend-request', handleSendFriendRequest);

// ── POST /api/add-friend (legacy alias for send-friend-request) ───────────────
app.post('/api/add-friend', handleSendFriendRequest);

// ── Helper: accept a friend request ──────────────────────────────────────────
async function acceptFriendRequest(userLower, fromLower) {
    // Remove from recipient's incoming requests
    const requestsPath = `accounts/${userLower}/friend_requests.json`;
    const requestsFile = await getFile(requestsPath);
    if (!requestsFile) return { success: false, message: 'Friend request not found' };
    const updatedRequests = requestsFile.content.filter(r => r.from.toLowerCase() !== fromLower);
    if (updatedRequests.length === requestsFile.content.length) {
        return { success: false, message: 'Friend request not found' };
    }
    await putFile(requestsPath, updatedRequests, `Accept friend request from ${fromLower}`, requestsFile.sha);

    // Remove from sender's outgoing requests
    const sentPath = `accounts/${fromLower}/sent_requests.json`;
    const sentFile = await getFile(sentPath);
    if (sentFile) {
        const updatedSent = sentFile.content.filter(s => s.toLowerCase() !== userLower);
        await putFile(sentPath, updatedSent, `Friend request accepted by ${userLower}`, sentFile.sha);
    }

    // Add sender to recipient's friends
    const myFriendsPath = `accounts/${userLower}/friends.json`;
    const myFriendsFile = await getFile(myFriendsPath);
    const myFriends     = myFriendsFile ? [...myFriendsFile.content] : [];
    if (!myFriends.some(f => f.toLowerCase() === fromLower)) {
        const fromFile = await getFile(`accounts/${fromLower}/profile.json`);
        myFriends.push(fromFile ? fromFile.content.username : fromLower);
        await putFile(myFriendsPath, myFriends, `Add friend ${fromLower} for: ${userLower}`, myFriendsFile ? myFriendsFile.sha : undefined);
    }

    // Add recipient to sender's friends
    const theirFriendsPath = `accounts/${fromLower}/friends.json`;
    const theirFriendsFile = await getFile(theirFriendsPath);
    const theirFriends     = theirFriendsFile ? [...theirFriendsFile.content] : [];
    if (!theirFriends.some(f => f.toLowerCase() === userLower)) {
        const userFile = await getFile(`accounts/${userLower}/profile.json`);
        theirFriends.push(userFile ? userFile.content.username : userLower);
        await putFile(theirFriendsPath, theirFriends, `Add friend ${userLower} for: ${fromLower}`, theirFriendsFile ? theirFriendsFile.sha : undefined);
    }

    return { success: true };
}

// ── POST /api/accept-friend-request ──────────────────────────────────────────
app.post('/api/accept-friend-request', async (req, res) => {
    try {
        const { username, fromUsername } = req.body;
        if (!username || !fromUsername) {
            return res.status(400).json({ success: false, message: 'Missing required fields' });
        }

        const fromFile = await getFile(`accounts/${fromUsername.toLowerCase()}/profile.json`);
        if (!fromFile) return res.status(404).json({ success: false, message: 'User not found' });

        const result = await acceptFriendRequest(username.toLowerCase(), fromUsername.toLowerCase());
        if (!result.success) return res.status(400).json(result);
        res.json({ success: true, message: `You are now friends with ${fromFile.content.username}!` });
    } catch (err) {
        console.error('Error accepting friend request:', err);
        res.status(500).json({ success: false, message: 'Failed to accept friend request. Please try again.' });
    }
});

// ── POST /api/decline-friend-request ─────────────────────────────────────────
app.post('/api/decline-friend-request', async (req, res) => {
    try {
        const { username, fromUsername } = req.body;
        if (!username || !fromUsername) {
            return res.status(400).json({ success: false, message: 'Missing required fields' });
        }

        const userLower = username.toLowerCase();
        const fromLower = fromUsername.toLowerCase();

        // Remove from recipient's incoming requests
        const requestsPath = `accounts/${userLower}/friend_requests.json`;
        const requestsFile = await getFile(requestsPath);
        if (!requestsFile) {
            return res.status(404).json({ success: false, message: 'Friend request not found' });
        }
        const updatedRequests = requestsFile.content.filter(r => r.from.toLowerCase() !== fromLower);
        await putFile(requestsPath, updatedRequests, `Decline friend request from ${fromUsername}`, requestsFile.sha);

        // Remove from sender's outgoing requests
        const sentPath = `accounts/${fromLower}/sent_requests.json`;
        const sentFile = await getFile(sentPath);
        if (sentFile) {
            const updatedSent = sentFile.content.filter(s => s.toLowerCase() !== userLower);
            await putFile(sentPath, updatedSent, `Friend request declined by ${username}`, sentFile.sha);
        }

        res.json({ success: true, message: 'Friend request declined.' });
    } catch (err) {
        console.error('Error declining friend request:', err);
        res.status(500).json({ success: false, message: 'Failed to decline friend request. Please try again.' });
    }
});

// ── POST /api/cancel-friend-request ──────────────────────────────────────────
app.post('/api/cancel-friend-request', async (req, res) => {
    try {
        const { username, toUsername } = req.body;
        if (!username || !toUsername) {
            return res.status(400).json({ success: false, message: 'Missing required fields' });
        }

        const userLower = username.toLowerCase();
        const toLower   = toUsername.toLowerCase();

        // Remove from sender's outgoing requests
        const sentPath = `accounts/${userLower}/sent_requests.json`;
        const sentFile = await getFile(sentPath);
        if (!sentFile) {
            return res.status(404).json({ success: false, message: 'No sent requests found' });
        }
        const updatedSent = sentFile.content.filter(s => s.toLowerCase() !== toLower);
        await putFile(sentPath, updatedSent, `Cancel friend request to ${toUsername}`, sentFile.sha);

        // Remove from recipient's incoming requests
        const theirRequestsPath = `accounts/${toLower}/friend_requests.json`;
        const theirRequestsFile = await getFile(theirRequestsPath);
        if (theirRequestsFile) {
            const updatedRequests = theirRequestsFile.content.filter(r => r.from.toLowerCase() !== userLower);
            await putFile(theirRequestsPath, updatedRequests, `Friend request cancelled by ${username}`, theirRequestsFile.sha);
        }

        res.json({ success: true, message: 'Friend request cancelled.' });
    } catch (err) {
        console.error('Error cancelling friend request:', err);
        res.status(500).json({ success: false, message: 'Failed to cancel friend request. Please try again.' });
    }
});

// ── GET /api/get-friend-requests ──────────────────────────────────────────────
app.get('/api/get-friend-requests', async (req, res) => {
    try {
        const { username } = req.query;
        if (!username || !sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username' });
        }
        const requestsFile = await getFile(`accounts/${username.toLowerCase()}/friend_requests.json`);
        res.json({ success: true, requests: requestsFile ? requestsFile.content : [] });
    } catch (err) {
        console.error('Error getting friend requests:', err);
        res.status(500).json({ success: false, message: 'Failed to get friend requests.' });
    }
});

// ── GET /api/get-sent-requests ────────────────────────────────────────────────
app.get('/api/get-sent-requests', async (req, res) => {
    try {
        const { username } = req.query;
        if (!username || !sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username' });
        }
        const sentFile = await getFile(`accounts/${username.toLowerCase()}/sent_requests.json`);
        res.json({ success: true, sent: sentFile ? sentFile.content : [] });
    } catch (err) {
        console.error('Error getting sent requests:', err);
        res.status(500).json({ success: false, message: 'Failed to get sent requests.' });
    }
});

// ── GET /api/get-friends ──────────────────────────────────────────────────────
app.get('/api/get-friends', async (req, res) => {
    try {
        const { username } = req.query;
        if (!username || !sanitiseUsername(username)) {
            return res.status(400).json({ success: false, message: 'Invalid username' });
        }
        const friendsFile = await getFile(`accounts/${username.toLowerCase()}/friends.json`);
        res.json({ success: true, friends: friendsFile ? friendsFile.content : [] });
    } catch (err) {
        console.error('Error getting friends:', err);
        res.status(500).json({ success: false, message: 'Failed to get friends list.' });
    }
});

// ── POST /api/remove-friend ───────────────────────────────────────────────────
app.post('/api/remove-friend', async (req, res) => {
    try {
        const { username, friendUsername } = req.body;

        if (!username || !friendUsername) {
            return res.status(400).json({ success: false, message: 'Missing required fields' });
        }

        const userLower   = username.toLowerCase();
        const friendLower = friendUsername.toLowerCase();

        // Remove friend from the requesting user's list
        const friendsPath = `accounts/${userLower}/friends.json`;
        const friendsFile = await getFile(friendsPath);
        if (!friendsFile) {
            return res.status(404).json({ success: false, message: 'Friends list not found' });
        }

        const updated = friendsFile.content.filter(f => f.toLowerCase() !== friendLower);
        await putFile(
            friendsPath,
            updated,
            `Remove friend ${friendUsername} for: ${username}`,
            friendsFile.sha
        );

        // Also remove the requesting user from the friend's list
        const theirFriendsPath = `accounts/${friendLower}/friends.json`;
        const theirFriendsFile = await getFile(theirFriendsPath);
        if (theirFriendsFile) {
            const theirUpdated = theirFriendsFile.content.filter(f => f.toLowerCase() !== userLower);
            await putFile(
                theirFriendsPath,
                theirUpdated,
                `Remove friend ${username} for: ${friendUsername}`,
                theirFriendsFile.sha
            );
        }

        res.json({ success: true, message: 'Friend removed' });
    } catch (err) {
        console.error('Error removing friend:', err);
        res.status(500).json({ success: false, message: 'Failed to remove friend. Please try again.' });
    }
});

// ── Messaging helpers ─────────────────────────────────────────────────────────

/**
 * Returns the canonical conversation path for two users.
 * Usernames are sorted alphabetically so both orderings resolve to the same file.
 */
function conversationPath(userA, userB) {
    const [a, b] = [userA.toLowerCase(), userB.toLowerCase()].sort();
    return `accounts/messages/${a}_${b}.json`;
}

/** Returns true when userA and userB are mutual friends. */
async function areFriends(userLower, friendLower) {
    const friendsFile = await getFile(`accounts/${userLower}/friends.json`);
    const friends = friendsFile ? friendsFile.content : [];
    return friends.some(f => f.toLowerCase() === friendLower);
}

// ── POST /api/send-message ────────────────────────────────────────────────────
app.post('/api/send-message', async (req, res) => {
    try {
        const { username, toUsername, text } = req.body;

        if (!username || !toUsername || !text || !text.trim()) {
            return res.status(400).json({ success: false, message: 'Missing required fields' });
        }

        const msgText = text.trim();
        if (msgText.length > 1000) {
            return res.status(400).json({ success: false, message: 'Message too long (max 1000 characters)' });
        }

        const userLower   = username.toLowerCase();
        const toLower     = toUsername.toLowerCase();

        if (userLower === toLower) {
            return res.status(400).json({ success: false, message: 'Cannot message yourself' });
        }

        // Verify both users exist and are friends
        const userFile = await getFile(`accounts/${userLower}/profile.json`);
        if (!userFile) return res.status(404).json({ success: false, message: 'Your account was not found' });

        if (!(await areFriends(userLower, toLower))) {
            return res.status(403).json({ success: false, message: 'You can only message friends' });
        }

        // Append message to the shared conversation file
        const convPath = conversationPath(userLower, toLower);
        const convFile = await getFile(convPath);
        const messages = convFile ? [...convFile.content] : [];
        messages.push({ from: userFile.content.username, text: msgText, sentAt: new Date().toISOString() });

        await putFile(convPath, messages, `Message from ${username} to ${toUsername}`, convFile ? convFile.sha : undefined);

        res.json({ success: true, message: 'Message sent' });
    } catch (err) {
        console.error('Error sending message:', err);
        res.status(500).json({ success: false, message: 'Failed to send message. Please try again.' });
    }
});

// ── GET /api/get-messages ─────────────────────────────────────────────────────
app.get('/api/get-messages', async (req, res) => {
    try {
        const { username, withUsername } = req.query;

        if (!username || !sanitiseUsername(username) || !withUsername || !sanitiseUsername(withUsername)) {
            return res.status(400).json({ success: false, message: 'Invalid parameters' });
        }

        if (!(await areFriends(username.toLowerCase(), withUsername.toLowerCase()))) {
            return res.status(403).json({ success: false, message: 'You can only read messages with friends' });
        }

        const convPath = conversationPath(username, withUsername);
        const convFile = await getFile(convPath);
        res.json({ success: true, messages: convFile ? convFile.content : [] });
    } catch (err) {
        console.error('Error getting messages:', err);
        res.status(500).json({ success: false, message: 'Failed to get messages.' });
    }
});

// ── POST /api/send-invite ─────────────────────────────────────────────────────
// Allows a C# app (via the shared PUBLIC_API_KEY) or an authenticated user
// to send a game invite to any registered user.
app.post('/api/send-invite', authenticatePublicOrUserToken, async (req, res) => {
    try {
        const { toUsername, gameName, inviteId } = req.body;
        if (!toUsername || !gameName || !gameName.trim()) {
            return res.status(400).json({ success: false, message: 'toUsername and gameName are required' });
        }
        const toLower = toUsername.toLowerCase();
        if (!sanitiseUsername(toLower)) {
            return res.status(400).json({ success: false, message: 'Invalid username' });
        }

        const accountFile = await getFile(`accounts/${toLower}/profile.json`);
        if (!accountFile) return res.status(404).json({ success: false, message: 'User not found' });

        const fromName = req.tokenUser.publicKeyAuth ? PUBLIC_INVITE_SENDER : req.tokenUser.username;
        const id = (inviteId && typeof inviteId === 'string' && /^[\w-]{1,64}$/.test(inviteId))
            ? inviteId
            : generateInviteId();

        const invitesPath = `accounts/${toLower}/invites.json`;
        const invitesFile = await getFile(invitesPath);
        const invites     = invitesFile ? [...invitesFile.content] : [];
        invites.push({
            inviteId: id,
            from:     fromName,
            gameName: gameName.trim(),
            sentAt:   new Date().toISOString(),
            status:   'pending'
        });
        await putFile(invitesPath, invites,
            `Invite from ${fromName} to ${toUsername} for ${gameName}`,
            invitesFile ? invitesFile.sha : undefined);

        res.json({ success: true, inviteId: id, message: 'Invite sent' });
    } catch (err) {
        console.error('Error sending invite:', err);
        res.status(500).json({ success: false, message: 'Failed to send invite. Please try again.' });
    }
});

// ── GET /api/get-invites ──────────────────────────────────────────────────────
app.get('/api/get-invites', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const file    = await getFile(`accounts/${usernameLower}/invites.json`);
        const invites = file ? file.content : [];
        res.json({ success: true, invites });
    } catch (err) {
        console.error('Error getting invites:', err);
        res.status(500).json({ success: false, message: 'Failed to get invites.' });
    }
});

// ── POST /api/respond-invite ──────────────────────────────────────────────────
app.post('/api/respond-invite', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const { inviteId, response } = req.body;
        if (!inviteId || !['accepted', 'declined'].includes(response)) {
            return res.status(400).json({ success: false, message: 'inviteId and response (accepted|declined) are required' });
        }

        const path = `accounts/${usernameLower}/invites.json`;
        const file = await getFile(path);
        if (!file) return res.status(404).json({ success: false, message: 'Invite not found' });

        const idx = file.content.findIndex(i => i.inviteId === inviteId);
        if (idx === -1) return res.status(404).json({ success: false, message: 'Invite not found' });

        const invites   = [...file.content];
        invites[idx]    = { ...invites[idx], status: response, respondedAt: new Date().toISOString() };
        await putFile(path, invites, `Invite ${response}: ${inviteId}`, file.sha);

        res.json({ success: true, message: `Invite ${response}` });
    } catch (err) {
        console.error('Error responding to invite:', err);
        res.status(500).json({ success: false, message: 'Failed to respond to invite. Please try again.' });
    }
});

// ── POST /api/reset-all-accounts ─────────────────────────────────────────────
app.post('/api/reset-all-accounts', async (req, res) => {
    try {
        const { adminKey } = req.body;
        if (!adminKey || !process.env.ADMIN_KEY || adminKey !== process.env.ADMIN_KEY) {
            return res.status(403).json({ success: false, message: 'Unauthorized' });
        }

        // List all items in the accounts directory
        let items;
        try {
            const { data } = await octokit.repos.getContent({
                owner: REPO_OWNER,
                repo:  REPO_NAME,
                path:  'accounts'
            });
            items = Array.isArray(data) ? data : [data];
        } catch (err) {
            if (err.status === 404) {
                return res.json({ success: true, message: 'No accounts to remove' });
            }
            throw err;
        }

        const committer = { name: 'Game OS Bot', email: 'bot@gameos.com' };

        // Delete every file; recurse one level into user sub-folders
        // Preserve the Admin.GameOS account
        for (const item of items) {
            if (item.name && item.name.toLowerCase() === ADMIN_USERNAME_LOWER) continue;
            if (item.type === 'dir') {
                try {
                    const { data: files } = await octokit.repos.getContent({
                        owner: REPO_OWNER,
                        repo:  REPO_NAME,
                        path:  item.path
                    });
                    for (const file of (Array.isArray(files) ? files : [files])) {
                        await octokit.repos.deleteFile({
                            owner: REPO_OWNER,
                            repo:  REPO_NAME,
                            path:  file.path,
                            message: `Reset: delete ${file.path}`,
                            sha:   file.sha,
                            committer
                        });
                    }
                } catch (e) {
                    if (e.status !== 404) throw e;
                }
            } else {
                await octokit.repos.deleteFile({
                    owner: REPO_OWNER,
                    repo:  REPO_NAME,
                    path:  item.path,
                    message: `Reset: delete ${item.path}`,
                    sha:   item.sha,
                    committer
                });
            }
        }

        res.json({ success: true, message: 'All accounts removed successfully' });
    } catch (err) {
        console.error('Error resetting accounts:', err);
        res.status(500).json({ success: false, message: 'Failed to reset accounts. Please try again.' });
    }
});

// ── POST /api/auth/token ──────────────────────────────────────────────────────
// Exchange username + password for a Game OS API token.
// Used by C# programs and other clients that need long-lived programmatic access.
app.post('/api/auth/token', async (req, res) => {
    const ip = req.ip || (req.connection && req.connection.remoteAddress) || 'unknown';
    if (!checkRateLimit(ip, RATE_LIMIT_AUTH)) {
        return res.status(429).json({ success: false, message: 'Too many token requests – wait a minute and try again.' });
    }

    try {
        const { username: identifier, password } = req.body;
        if (!identifier || !password) {
            return res.status(400).json({ success: false, message: 'username and password are required.' });
        }

        // Resolve identifier (email or username) to account key
        let accountKey;
        if (identifier.includes('@')) {
            const emailIndexFile = await getFile('accounts/email-index.json');
            if (!emailIndexFile) return res.status(401).json({ success: false, message: 'Account not found.' });
            accountKey = emailIndexFile.content[identifier.toLowerCase()];
            if (!accountKey) return res.status(401).json({ success: false, message: 'Account not found.' });
        } else {
            accountKey = identifier.toLowerCase();
        }

        const accountFile = await getFile(`accounts/${accountKey}/profile.json`);
        if (!accountFile) return res.status(401).json({ success: false, message: 'Account not found.' });

        // Support both bcrypt (backend-created) and PBKDF2 (frontend-created) password hashes
        const valid = await verifyPassword(password, accountFile.content.password_hash, accountFile.content.username);
        if (!valid) return res.status(401).json({ success: false, message: 'Invalid password.' });

        // Generate a fresh token
        const rawToken   = generateRawToken(accountKey);
        const tokenHash  = hashToken(rawToken);
        const issuedAt   = new Date().toISOString();

        // Persist hash in the user's profile
        const updated = {
            ...accountFile.content,
            api_token_hash:       tokenHash,
            api_token_issued_at:  issuedAt
        };
        await putFile(
            `accounts/${accountKey}/profile.json`,
            updated,
            `Issue API token for: ${accountKey}`,
            accountFile.sha
        );

        // Cache the token immediately so authenticated requests succeed even if
        // GitHub's API hasn't yet returned the updated profile (propagation delay).
        _cacheToken(rawToken, accountKey, accountFile.content.username, accountFile.content.email);

        res.json({
            success:  true,
            token:    rawToken,
            username: accountFile.content.username,
            issuedAt
        });
    } catch (err) {
        console.error('Error issuing token:', err);
        res.status(500).json({ success: false, message: 'Failed to issue token. Please try again.' });
    }
});

// ── POST /api/auth/revoke-token ───────────────────────────────────────────────
// Revoke the caller's API token (requires password confirmation).
app.post('/api/auth/revoke-token', async (req, res) => {
    const ip = req.ip || (req.connection && req.connection.remoteAddress) || 'unknown';
    if (!checkRateLimit(ip, RATE_LIMIT_AUTH)) {
        return res.status(429).json({ success: false, message: 'Too many requests – wait a minute and try again.' });
    }

    try {
        const { username: identifier, password } = req.body;
        if (!identifier || !password) {
            return res.status(400).json({ success: false, message: 'username and password are required.' });
        }

        let accountKey;
        if (identifier.includes('@')) {
            const emailIndexFile = await getFile('accounts/email-index.json');
            if (!emailIndexFile) return res.status(401).json({ success: false, message: 'Account not found.' });
            accountKey = emailIndexFile.content[identifier.toLowerCase()];
            if (!accountKey) return res.status(401).json({ success: false, message: 'Account not found.' });
        } else {
            accountKey = identifier.toLowerCase();
        }

        const accountFile = await getFile(`accounts/${accountKey}/profile.json`);
        if (!accountFile) return res.status(401).json({ success: false, message: 'Account not found.' });

        // Support both bcrypt (backend-created) and PBKDF2 (frontend-created) password hashes
        const validR = await verifyPassword(password, accountFile.content.password_hash, accountFile.content.username);
        if (!validR) return res.status(401).json({ success: false, message: 'Invalid password.' });

        const updated = { ...accountFile.content };
        delete updated.api_token_hash;
        delete updated.api_token_issued_at;
        await putFile(
            `accounts/${accountKey}/profile.json`,
            updated,
            `Revoke API token for: ${accountKey}`,
            accountFile.sha
        );

        // Evict any cached tokens for this account so they're immediately invalid
        _evictTokensForUser(accountKey);

        res.json({ success: true, message: 'API token revoked successfully.' });
    } catch (err) {
        console.error('Error revoking token:', err);
        res.status(500).json({ success: false, message: 'Failed to revoke token. Please try again.' });
    }
});

// ── GET /api/me ───────────────────────────────────────────────────────────────
// Return the authenticated user's public profile.
app.get('/api/me', authenticateToken, async (req, res) => {
    try {
        const { username, email, usernameLower } = req.tokenUser;
        const accountFile = await getFile(`accounts/${usernameLower}/profile.json`);
        if (!accountFile) return res.status(404).json({ success: false, message: 'Account not found.' });
        const { password_hash, api_token_hash, ...profile } = accountFile.content;
        res.json({ success: true, profile });
    } catch (err) {
        console.error('GET /api/me error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/me/games ─────────────────────────────────────────────────────────
app.get('/api/me/games', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const file = await getFile(`accounts/${usernameLower}/games.json`);
        res.json({ success: true, games: file ? file.content : [] });
    } catch (err) {
        console.error('GET /api/me/games error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── POST /api/me/games ────────────────────────────────────────────────────────
// Add a game to the authenticated user's library.
// Body: { platform, title, titleId? }
app.post('/api/me/games', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const { platform, title, titleId, coverUrl, steamAppId, playtimeMinutes } = req.body;
        if (!platform || !title) {
            return res.status(400).json({ success: false, message: 'platform and title are required.' });
        }

        const path    = `accounts/${usernameLower}/games.json`;
        const file    = await getFile(path);
        const library = file ? [...file.content] : [];

        const alreadyOwned = library.some(
            g => g.platform === platform && (g.title || '').toLowerCase() === title.toLowerCase()
        );
        if (alreadyOwned) {
            return res.status(400).json({ success: false, message: 'Game already in library.' });
        }

        let parsedSteamAppId = null;
        if (steamAppId !== undefined && steamAppId !== null && steamAppId !== '') {
            const n = parseInt(steamAppId, 10);
            if (!isNaN(n) && n > 0) parsedSteamAppId = n;
        }

        let parsedPlaytimeMinutes = 0;
        if (playtimeMinutes !== undefined && playtimeMinutes !== null && playtimeMinutes !== '') {
            const n = parseInt(playtimeMinutes, 10);
            if (!isNaN(n) && n >= 0) parsedPlaytimeMinutes = n;
        }

        library.push({
            platform,
            title,
            titleId: titleId || null,
            coverUrl: typeof coverUrl === 'string' ? coverUrl.trim() || null : null,
            steamAppId: parsedSteamAppId,
            playtimeMinutes: parsedPlaytimeMinutes,
            addedAt: new Date().toISOString()
        });

        await putFile(path, library, `Add game: ${title} (${platform})`, file ? file.sha : undefined);
        res.json({ success: true, message: 'Game added to library.', games: library });
    } catch (err) {
        console.error('POST /api/me/games error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── DELETE /api/me/games ──────────────────────────────────────────────────────
// Remove a game from the authenticated user's library.
// Body: { platform, title }
app.delete('/api/me/games', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const { platform, title } = req.body;
        if (!platform || !title) {
            return res.status(400).json({ success: false, message: 'platform and title are required.' });
        }

        const path = `accounts/${usernameLower}/games.json`;
        const file = await getFile(path);
        if (!file) return res.status(404).json({ success: false, message: 'Game library not found.' });

        const updated = file.content.filter(
            g => !(g.platform === platform && (g.title || '').toLowerCase() === title.toLowerCase())
        );
        await putFile(path, updated, `Remove game: ${title} (${platform})`, file.sha);
        res.json({ success: true, message: 'Game removed from library.', games: updated });
    } catch (err) {
        console.error('DELETE /api/me/games error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── PATCH /api/me/profile ─────────────────────────────────────────────────────
// Update editable public profile fields persisted in profile.json.
// Accepted fields: gamerScore (int), steamUserId (string), totalGames (int).
// Omitted fields are left unchanged.  Sensitive fields (password_hash, api_token_hash)
// are never exposed or modified by this endpoint.
app.patch('/api/me/profile', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const { gamerScore, steamUserId, totalGames } = req.body;

        const accountFile = await getFile(`accounts/${usernameLower}/profile.json`);
        if (!accountFile) return res.status(404).json({ success: false, message: 'Account not found.' });

        const profile = { ...accountFile.content };
        if (gamerScore  !== undefined) profile.gamerScore  = Number.isFinite(Number(gamerScore))  ? Math.max(0, Math.trunc(Number(gamerScore)))  : (profile.gamerScore  ?? 0);
        if (totalGames  !== undefined) profile.totalGames  = Number.isFinite(Number(totalGames))  ? Math.max(0, Math.trunc(Number(totalGames)))  : (profile.totalGames  ?? 0);
        if (steamUserId !== undefined) {
            await writeProfileWithSteamBinding(
                usernameLower,
                profile,
                accountFile.sha,
                steamUserId,
                'Profile update');
        } else {
            await putFile(`accounts/${usernameLower}/profile.json`, profile,
                `Profile update: ${usernameLower}`, accountFile.sha);
        }

        const { password_hash, api_token_hash, ...safeProfile } = profile;
        res.json({ success: true, profile: safeProfile });
    } catch (err) {
        console.error('PATCH /api/me/profile error:', err);
        res.status(err.statusCode || 500).json({ success: false, message: err.message || 'Server error.' });
    }
});

// ── POST /api/link-steam ───────────────────────────────────────────────────────
app.post('/api/link-steam', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const { steamUserId } = req.body || {};
        const accountFile = await getFile(`accounts/${usernameLower}/profile.json`);
        if (!accountFile) return res.status(404).json({ success: false, message: 'Account not found.' });

        const updatedProfile = await writeProfileWithSteamBinding(
            usernameLower,
            { ...accountFile.content },
            accountFile.sha,
            steamUserId,
            'Steam ID link');

        const { password_hash, api_token_hash, ...safeProfile } = updatedProfile;
        res.json({ success: true, profile: safeProfile, steamUserId: updatedProfile.steamUserId || '' });
    } catch (err) {
        console.error('POST /api/link-steam error:', err);
        res.status(err.statusCode || 500).json({ success: false, message: err.message || 'Server error.' });
    }
});

// ── PATCH /api/me/games/playtime ──────────────────────────────────────────────
// Update a game's accumulated playtime and lastPlayedAt in games.json so other
// devices see the new data on their next sync tick without needing to aggregate
// the full activity log.
// Body: { platform, title, playtimeMinutes, lastPlayedAt }
app.patch('/api/me/games/playtime', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const { platform, title, playtimeMinutes, lastPlayedAt } = req.body;
        if (!platform || !title || playtimeMinutes === undefined) {
            return res.status(400).json({ success: false, message: 'platform, title, and playtimeMinutes are required.' });
        }
        const minutes = parseInt(playtimeMinutes, 10);
        if (isNaN(minutes) || minutes < 0) {
            return res.status(400).json({ success: false, message: 'playtimeMinutes must be a non-negative number.' });
        }

        const path = `accounts/${usernameLower}/games.json`;
        const file = await getFile(path);
        if (!file) return res.status(404).json({ success: false, message: 'Game library not found.' });

        const library = [...file.content];
        const game = library.find(
            g => g.platform === platform && (g.title || '').toLowerCase() === title.toLowerCase()
        );
        if (!game) {
            // Game not in library — silently succeed (local-only ROM not yet added to cloud library)
            return res.json({ success: true, message: 'Game not in cloud library — playtime not stored.' });
        }

        // Only advance the playtime forward (never decrease it from a stale client write)
        if (minutes > (game.playtimeMinutes || 0)) game.playtimeMinutes = minutes;
        // Only advance lastPlayedAt forward (ISO 8601 strings sort lexicographically)
        if (lastPlayedAt && (!game.lastPlayedAt || lastPlayedAt > game.lastPlayedAt))
            game.lastPlayedAt = lastPlayedAt;

        await putFile(path, library,
            `Playtime: ${title} (${platform}) – ${game.playtimeMinutes}min`,
            file.sha);
        res.json({ success: true, message: 'Playtime updated.', game });
    } catch (err) {
        console.error('PATCH /api/me/games/playtime error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/me/wishlist ──────────────────────────────────────────────────────
app.get('/api/me/wishlist', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const file = await getFile(`accounts/${usernameLower}/wishlist.json`);
        res.json({ success: true, wishlist: file ? file.content : [] });
    } catch (err) {
        console.error('GET /api/me/wishlist error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── POST /api/me/wishlist ─────────────────────────────────────────────────────
// Add a game to the authenticated user's wishlist.
// Body: { platform, title, titleId? }
app.post('/api/me/wishlist', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const { platform, title, titleId, coverUrl } = req.body;
        if (!platform || !title) {
            return res.status(400).json({ success: false, message: 'platform and title are required.' });
        }

        const path     = `accounts/${usernameLower}/wishlist.json`;
        const file     = await getFile(path);
        const wishlist = file ? [...file.content] : [];

        const alreadyWishlisted = wishlist.some(
            g => g.platform === platform && (g.title || '').toLowerCase() === title.toLowerCase()
        );
        if (alreadyWishlisted) {
            return res.status(400).json({ success: false, message: 'Game already in wishlist.' });
        }

        wishlist.push({
            platform,
            title,
            titleId:  titleId || null,
            coverUrl: coverUrl || undefined,
            addedAt:  new Date().toISOString()
        });

        await putFile(path, wishlist, `Add to wishlist: ${title} (${platform})`, file ? file.sha : undefined);
        res.json({ success: true, message: 'Game added to wishlist.', wishlist });
    } catch (err) {
        console.error('POST /api/me/wishlist error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── DELETE /api/me/wishlist ───────────────────────────────────────────────────
// Remove a game from the authenticated user's wishlist.
// Body: { platform, title }
app.delete('/api/me/wishlist', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const { platform, title } = req.body;
        if (!platform || !title) {
            return res.status(400).json({ success: false, message: 'platform and title are required.' });
        }

        const path = `accounts/${usernameLower}/wishlist.json`;
        const file = await getFile(path);
        if (!file) return res.json({ success: true, message: 'Game removed from wishlist.', wishlist: [] });

        const updated = file.content.filter(
            g => !(g.platform === platform && (g.title || '').toLowerCase() === title.toLowerCase())
        );
        await putFile(path, updated, `Remove from wishlist: ${title} (${platform})`, file.sha);
        res.json({ success: true, message: 'Game removed from wishlist.', wishlist: updated });
    } catch (err) {
        console.error('DELETE /api/me/wishlist error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/me/achievements ──────────────────────────────────────────────────
app.get('/api/me/achievements', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const file = await getFile(`accounts/${usernameLower}/achievements.json`);
        res.json({ success: true, achievements: file ? file.content : [] });
    } catch (err) {
        console.error('GET /api/me/achievements error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── POST /api/me/achievements ─────────────────────────────────────────────────
// Unlock (or update) an achievement for the authenticated user.
// Body: { platform, gameTitle, achievementId, name, description?, unlockedAt? }
app.post('/api/me/achievements', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const { platform, gameTitle, titleId, achievementId, name, description, unlockedAt } = req.body;
        if (!platform || !gameTitle || !achievementId || !name) {
            return res.status(400).json({ success: false, message: 'platform, gameTitle, achievementId, and name are required.' });
        }
        const normalizedPlatform = normalizePlatformName(platform);

        const path = `accounts/${usernameLower}/achievements.json`;
        const file = await getFile(path);
        const list = file ? [...file.content] : [];

        const existing = list.findIndex(
            a => normalizePlatformName(a.platform) === normalizedPlatform &&
                 (a.gameTitle || '').toLowerCase() === gameTitle.toLowerCase() &&
                 String(a.achievementId) === String(achievementId)
        );

        const entry = {
            platform: normalizedPlatform,
            gameTitle,
            achievementId: String(achievementId),
            name,
            description: description || '',
            unlockedAt: unlockedAt || new Date().toISOString()
        };

        if (existing !== -1) {
            list[existing] = { ...list[existing], ...entry };
        } else {
            list.push(entry);
        }

        await putFile(path, list, `Achievement: ${name} in ${gameTitle} (${platform})`, file ? file.sha : undefined);

        // Mirror per-game achievements for cross-device clients that read by platform/titleId path.
        // Path format: accounts/{username}/Achievements/{PlatformName}/{TitleIdOrTitle}/achievements.json
        // The legacy misspelled path (Achivements) is read as a one-way migration source but never written.
        try {
            const platformKey = sanitisePathSegment(normalizedPlatform, 'unknown-platform');
            const titleKey = await resolveAchievementTitleKey(usernameLower, normalizedPlatform, gameTitle, titleId);
            const canonicalPath = `accounts/${usernameLower}/Achievements/${platformKey}/${titleKey}/achievements.json`;
            const legacyPath = `accounts/${usernameLower}/Achivements/${platformKey}/${titleKey}/achievements.json`;
            const canonicalFile = await getFile(canonicalPath);
            const legacyFile = canonicalFile ? null : await getFile(legacyPath);
            const sourceList = Array.isArray(canonicalFile?.content)
                ? canonicalFile.content
                : (Array.isArray(legacyFile?.content) ? legacyFile.content : []);
            const byGameList = [...sourceList];

            const existingByGame = byGameList.findIndex(
                a => String(a.achievementId) === String(achievementId)
            );
            if (existingByGame !== -1) {
                byGameList[existingByGame] = { ...byGameList[existingByGame], ...entry };
            } else {
                byGameList.push(entry);
            }

            await putFile(
                canonicalPath,
                byGameList,
                `Achievement mirror: ${name} in ${gameTitle} (${platform})`,
                canonicalFile ? canonicalFile.sha : undefined
            );
        } catch (mirrorErr) {
            console.warn('POST /api/me/achievements mirror write failed:', mirrorErr.message);
        }

        res.json({ success: true, message: 'Achievement recorded.', achievement: entry });
    } catch (err) {
        console.error('POST /api/me/achievements error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── PUT /api/me/achievements/game-template ────────────────────────────────────
// Writes the full achievement list (locked + unlocked) for a specific game to the
// per-game folder in the user's private cloud repo.
// Path: accounts/{username}/Achievements/{platform}/{titleKey}/achievements.json
// Unlike POST /api/me/achievements this does NOT touch the global achievements.json
// and accepts locked achievements (empty unlockedAt).
// Body: { platform, gameTitle, titleKey, achievements: [{achievementId, name, description, unlockedAt?}] }
app.put('/api/me/achievements/game-template', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const { platform, gameTitle, titleKey, titleId, achievements } = req.body;
        if (!platform || !gameTitle || !titleKey || !Array.isArray(achievements) || achievements.length === 0) {
            return res.status(400).json({ success: false, message: 'platform, gameTitle, titleKey, and achievements array are required (titleId optional).' });
        }
        const normalizedPlatform = normalizePlatformName(platform);
        const platformKey = sanitisePathSegment(normalizedPlatform, 'unknown-platform');
        const requestedKey = sanitisePathSegment(String(titleKey).trim(), 'unknown-title');
        const safeGameTitle = sanitisePathSegment(String(gameTitle).trim(), 'unknown-title');
        const isTitleFallbackKey =
            requestedKey.toLowerCase() === safeGameTitle.toLowerCase() ||
            String(titleKey).trim().toLowerCase() === String(gameTitle).trim().toLowerCase();
        const preferredKeyInput = isTitleFallbackKey ? titleId : (titleId || requestedKey);
        const resolvedRawKey = await resolveAchievementTitleKey(
            usernameLower,
            normalizedPlatform,
            gameTitle,
            preferredKeyInput
        );
        const safeKey = sanitisePathSegment(resolvedRawKey, 'unknown-title');
        const path = `accounts/${usernameLower}/Achievements/${platformKey}/${safeKey}/achievements.json`;
        const aliasPath = requestedKey !== safeKey
            ? `accounts/${usernameLower}/Achievements/${platformKey}/${requestedKey}/achievements.json`
            : null;

        const file = await getFile(path);
        const aliasFile = aliasPath ? await getFile(aliasPath) : null;

        // Merge canonical file + alias file + incoming template entries.
        // Existing unlockedAt values are preserved over empty incoming values.
        const merged = [];
        const upsertAchievement = (incoming) => {
            const rawAchievementId = incoming?.achievementId;
            const achId = rawAchievementId === null || rawAchievementId === undefined || rawAchievementId === ''
                ? String(incoming?.name || '')
                : String(rawAchievementId);
            if (!achId) return;
            const incomingName = String(incoming?.name || '');
            const idx = merged.findIndex(e =>
                String(e.achievementId || '') === achId ||
                (String(e.achievementId || '') === '' &&
                 incomingName !== '' &&
                 String(e.name || '').toLowerCase() === incomingName.toLowerCase()));
            const normalizedIncoming = { ...incoming, achievementId: achId };
            if (idx !== -1) {
                const existingUnlockedAt = typeof merged[idx].unlockedAt === 'string' ? merged[idx].unlockedAt : '';
                const incomingUnlockedAt = typeof normalizedIncoming.unlockedAt === 'string' ? normalizedIncoming.unlockedAt : '';
                const keepUnlockedAt = existingUnlockedAt !== '' ? existingUnlockedAt : incomingUnlockedAt;
                merged[idx] = { ...merged[idx], ...normalizedIncoming, unlockedAt: keepUnlockedAt };
            } else {
                merged.push(normalizedIncoming);
            }
        };

        if (Array.isArray(file?.content)) {
            for (const existing of file.content) upsertAchievement(existing);
        }
        if (Array.isArray(aliasFile?.content)) {
            for (const existing of aliasFile.content) upsertAchievement(existing);
        }

        for (const a of achievements) {
            upsertAchievement({
                platform: normalizedPlatform,
                gameTitle,
                achievementId: a.achievementId,
                name: a.name || '',
                description: a.description || '',
                unlockedAt: a.unlockedAt || '',
            });
        }

        await putFile(path, merged, `Sync achievements: ${gameTitle} (${platform})`, file ? file.sha : undefined);
        res.json({ success: true, message: 'Achievement template written.', count: merged.length });
    } catch (err) {
        console.error('PUT /api/me/achievements/game-template error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/me/friends ───────────────────────────────────────────────────────
app.get('/api/me/friends', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const file = await getFile(`accounts/${usernameLower}/friends.json`);
        res.json({ success: true, friends: file ? file.content : [] });
    } catch (err) {
        console.error('GET /api/me/friends error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── POST /api/me/activity ─────────────────────────────────────────────────────
// Log a game-play session or achievement unlock for the authenticated user.
// Body: { platform, gameTitle, titleId?, sessionStart, sessionEnd, minutesPlayed,
//         type?, achievementName?, achievementIcon? }
// type: "playtime" (default) | "achievement_unlocked"
app.post('/api/me/activity', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const { platform, gameTitle, titleId, sessionStart, sessionEnd, minutesPlayed,
                type, achievementName, achievementIcon } = req.body;

        const entryType = (type === 'achievement_unlocked') ? 'achievement_unlocked' : 'playtime';

        if (entryType === 'playtime') {
            if (!platform || !gameTitle || !sessionStart || minutesPlayed === undefined) {
                return res.status(400).json({ success: false, message: 'platform, gameTitle, sessionStart, and minutesPlayed are required.' });
            }
            const minutes = parseInt(minutesPlayed, 10);
            // Max 2880 minutes (48 hours) to support marathon sessions while preventing bad data
            if (isNaN(minutes) || minutes < 0 || minutes > 2880) {
                return res.status(400).json({ success: false, message: 'minutesPlayed must be a number between 0 and 2880 (48 hours).' });
            }

            const path = `accounts/${usernameLower}/activity.json`;
            const file = await getFile(path);
            const log  = file ? [...file.content] : [];

            // Keep only the last 500 sessions to prevent unbounded growth
            const entry = {
                type:          'playtime',
                platform,
                gameTitle,
                titleId:       titleId || null,
                sessionStart,
                sessionEnd:    sessionEnd || null,
                minutesPlayed: minutes,
                loggedAt:      new Date().toISOString()
            };
            log.push(entry);
            if (log.length > 500) log.splice(0, log.length - 500);

            await putFile(path, log, `Activity: ${gameTitle} (${platform}) – ${minutes}min`, file ? file.sha : undefined);
            return res.json({ success: true, message: 'Activity logged.', entry });
        }

        // ── achievement_unlocked ──────────────────────────────────────────────
        if (!platform || !gameTitle || !achievementName) {
            return res.status(400).json({ success: false, message: 'platform, gameTitle, and achievementName are required for achievement_unlocked.' });
        }

        const safeAchievementName = String(achievementName).slice(0, 200);
        const safeAchievementIcon = achievementIcon && typeof achievementIcon === 'string'
            ? achievementIcon.slice(0, 500) : null;

        const path = `accounts/${usernameLower}/activity.json`;
        const file = await getFile(path);
        const log  = file ? [...file.content] : [];

        const entry = {
            type:            'achievement_unlocked',
            platform,
            gameTitle,
            titleId:         titleId || null,
            achievementName: safeAchievementName,
            achievementIcon: safeAchievementIcon,
            loggedAt:        new Date().toISOString()
        };
        log.push(entry);
        if (log.length > 500) log.splice(0, log.length - 500);

        await putFile(path, log, `Achievement: ${safeAchievementName} in ${gameTitle} (${platform})`, file ? file.sha : undefined);
        res.json({ success: true, message: 'Achievement unlock logged.', entry });
    } catch (err) {
        console.error('POST /api/me/activity error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/me/activity ──────────────────────────────────────────────────────
app.get('/api/me/activity', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const file = await getFile(`accounts/${usernameLower}/activity.json`);
        const log  = file ? file.content : [];
        // Optional: filter by platform or gameTitle via query params
        const { platform, gameTitle, limit } = req.query;
        let result = log;
        if (platform) result = result.filter(e => e.platform === platform);
        if (gameTitle) result = result.filter(e => (e.gameTitle || '').toLowerCase() === gameTitle.toLowerCase());
        if (limit) result = result.slice(-Math.max(1, Math.min(500, parseInt(limit, 10))));
        res.json({ success: true, activity: result });
    } catch (err) {
        console.error('GET /api/me/activity error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── POST /api/me/sync-signal ──────────────────────────────────────────────────
// Writes a tiny { lastActivityAt } file that other open instances of the launcher
// poll every 30 seconds.  When the timestamp advances they immediately re-fetch
// playtime/recently-played data without waiting for the 5-minute full sync tick.
app.post('/api/me/sync-signal', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const path = `accounts/${usernameLower}/sync-signal.json`;
        const file = await getFile(path);
        const signal = { lastActivityAt: new Date().toISOString() };
        await putFile(path, signal, 'Sync signal', file ? file.sha : undefined);
        res.json({ success: true, signal });
    } catch (err) {
        console.error('POST /api/me/sync-signal error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/me/sync-signal ───────────────────────────────────────────────────
// Returns the current { lastActivityAt } so other devices can detect when a new
// session has been recorded and trigger an immediate data refresh.
app.get('/api/me/sync-signal', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const file = await getFile(`accounts/${usernameLower}/sync-signal.json`);
        res.json({ success: true, signal: file ? file.content : { lastActivityAt: null } });
    } catch (err) {
        console.error('GET /api/me/sync-signal error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/users/:username ──────────────────────────────────────────────────
// Read a user's public profile using either the shared PUBLIC_API_KEY or their
// own per-user token.  Sensitive fields (password hash, token hash) are stripped.
app.get('/api/users/:username', authenticatePublicOrUserToken, async (req, res) => {
    try {
        const targetUser = req.params.username;
        if (!targetUser || !sanitiseUsername(targetUser)) {
            return res.status(400).json({ success: false, message: 'Invalid username.' });
        }
        const accountFile = await getFile(`accounts/${targetUser.toLowerCase()}/profile.json`);
        if (!accountFile) return res.status(404).json({ success: false, message: 'User not found.' });
        // Use an explicit allowlist to avoid accidentally exposing future sensitive fields
        const { username, created_at } = accountFile.content;
        res.json({ success: true, profile: { username, created_at } });
    } catch (err) {
        console.error('GET /api/users/:username error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/users/:username/games ────────────────────────────────────────────
// Read any user's game library using either the public key or a per-user token.
app.get('/api/users/:username/games', authenticatePublicOrUserToken, async (req, res) => {
    try {
        const targetUser = req.params.username;
        if (!targetUser || !sanitiseUsername(targetUser)) {
            return res.status(400).json({ success: false, message: 'Invalid username.' });
        }
        const file = await getFile(`accounts/${targetUser.toLowerCase()}/games.json`);
        res.json({ success: true, games: file ? file.content : [] });
    } catch (err) {
        console.error('GET /api/users/:username/games error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/users/:username/achievements ─────────────────────────────────────
// Read any user's achievements using either the public key or a per-user token.
app.get('/api/users/:username/achievements', authenticatePublicOrUserToken, async (req, res) => {
    try {
        const targetUser = req.params.username;
        if (!targetUser || !sanitiseUsername(targetUser)) {
            return res.status(400).json({ success: false, message: 'Invalid username.' });
        }
        const file = await getFile(`accounts/${targetUser.toLowerCase()}/achievements.json`);
        res.json({ success: true, achievements: file ? file.content : [] });
    } catch (err) {
        console.error('GET /api/users/:username/achievements error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/users/:username/wishlist ─────────────────────────────────────────
// Read any user's wishlist using either the public key or a per-user token.
app.get('/api/users/:username/wishlist', authenticatePublicOrUserToken, async (req, res) => {
    try {
        const targetUser = req.params.username;
        if (!targetUser || !sanitiseUsername(targetUser)) {
            return res.status(400).json({ success: false, message: 'Invalid username.' });
        }
        const file = await getFile(`accounts/${targetUser.toLowerCase()}/wishlist.json`);
        res.json({ success: true, wishlist: file ? file.content : [] });
    } catch (err) {
        console.error('GET /api/users/:username/wishlist error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/users/:username/activity ────────────────────────────────────────
// Read any user's public activity log using either the public key or a per-user token.
app.get('/api/users/:username/activity', authenticatePublicOrUserToken, async (req, res) => {
    try {
        const targetUser = req.params.username;
        if (!targetUser || !sanitiseUsername(targetUser)) {
            return res.status(400).json({ success: false, message: 'Invalid username.' });
        }
        const file   = await getFile(`accounts/${targetUser.toLowerCase()}/activity.json`);
        const log    = file ? file.content : [];
        const { platform, gameTitle, limit } = req.query;
        let result = log;
        if (platform)   result = result.filter(e => e.platform === platform);
        if (gameTitle)  result = result.filter(e => (e.gameTitle || '').toLowerCase() === gameTitle.toLowerCase());
        if (limit)      result = result.slice(-Math.max(1, Math.min(500, parseInt(limit, 10))));
        res.json({ success: true, activity: result });
    } catch (err) {
        console.error('GET /api/users/:username/activity error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

function normaliseExophaseProfileId(profileId) {
    const trimmed = String(profileId || '').trim();
    if (!trimmed) return '';
    return trimmed.startsWith('#') ? trimmed : `#${trimmed}`;
}

function resolveExophaseUrlTemplate(exophaseUrl, profileId) {
    const urlText = String(exophaseUrl || '').trim();
    if (!urlText) return '';

    const normalisedProfileId = normaliseExophaseProfileId(profileId);
    if (!normalisedProfileId) return urlText;

    const profileDigits = normalisedProfileId.replace(/^#/, '');
    let resolved = urlText
        .replace(/#\$UserID/gi, normalisedProfileId)
        .replace(/\$UserID/gi, profileDigits)
        .replace(/\{#UserID\}/gi, normalisedProfileId)
        .replace(/\{UserID\}/gi, profileDigits)
        .replace(/#\$ExoID/gi, normalisedProfileId)
        .replace(/\$ExoID/gi, profileDigits)
        .replace(/\{#ExoID\}/gi, normalisedProfileId)
        .replace(/\{ExoID\}/gi, profileDigits);

    try {
        const parsed = new URL(resolved);
        if ((parsed.hostname === 'exophase.com' || parsed.hostname.endsWith('.exophase.com')) && !parsed.hash) {
            parsed.hash = profileDigits;
            resolved = parsed.toString();
        }
    } catch {
        // Keep resolved as-is; URL validation runs later in the route.
    }

    return resolved;
}

// ── POST /api/me/achievements/sync-exophase ───────────────────────────────────
// Fetch an Exophase game achievements/trophies page, scrape the list, and merge
// the results into the authenticated user's achievements.json file.
// Also writes the achievement template to Data/{folder}/Games/{titleId}/achievements.json
// in the Games.Database repository when titleId is supplied and GAMES_DB_TOKEN is set.
//
// Exophase page structure (verified against live site):
//   <ul class="achievement|trophy|challenge">
//     <li data-average="45.2" class="[secret]">
//       <img src="https://...icon.jpg">
//       <a>Achievement Name</a>
//       <div class="award-description"><p>Description</p></div>
//     </li>
//   </ul>
//
// Body: { exophaseUrl, platform, gameTitle, titleId? }
app.post('/api/me/achievements/sync-exophase', authenticateToken, async (req, res) => {
    try {
        const { usernameLower } = req.tokenUser;
        const { exophaseUrl, platform, gameTitle, titleId, exophaseProfileId } = req.body;

        if (!exophaseUrl || !platform || !gameTitle) {
            return res.status(400).json({ success: false, message: 'exophaseUrl, platform, and gameTitle are required.' });
        }

        const normalisedProfileId = normaliseExophaseProfileId(exophaseProfileId);
        const resolvedExophaseUrl = resolveExophaseUrlTemplate(exophaseUrl, normalisedProfileId);

        // SSRF protection: only allow https://exophase.com URLs
        let parsedUrl;
        try {
            parsedUrl = new URL(resolvedExophaseUrl);
        } catch {
            return res.status(400).json({ success: false, message: 'Invalid URL.' });
        }
        if (parsedUrl.protocol !== 'https:' ||
            (parsedUrl.hostname !== 'exophase.com' && !parsedUrl.hostname.endsWith('.exophase.com'))) {
            return res.status(400).json({ success: false, message: 'Only https://exophase.com URLs are allowed.' });
        }
        const hadFragment = Boolean(parsedUrl.hash);
        const fragmentProvidedByInput = String(exophaseUrl || '').includes('#');
        if (hadFragment) {
            // URL fragments (e.g. #4447906) are client-side only and are never sent in
            // the actual HTTP request. Keep profile ID separately; do not rely on hash.
            parsedUrl.hash = '';
            if (fragmentProvidedByInput)
                console.warn('sync-exophase: URL fragment supplied and ignored; using exophaseProfileId/body data instead.');
        }
        const validatedExophaseUrl = parsedUrl.toString();

        // Fetch the Exophase page with a 15-second timeout.
        // Exophase uses server-side rendering so a plain fetch returns the full HTML.
        // Use a real browser User-Agent to avoid being blocked.
        const controller = new AbortController();
        const fetchTimeout = setTimeout(() => controller.abort(), 15000);
        let html;
        try {
            const response = await fetch(validatedExophaseUrl, {
                signal: controller.signal,
                headers: {
                    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
                    'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
                    'Accept-Language': 'en-US,en;q=0.9'
                }
            });
            if (!response.ok) {
                return res.status(502).json({ success: false, message: `Failed to fetch Exophase page (HTTP ${response.status}).` });
            }
            html = await response.text();
        } catch (fetchErr) {
            if (fetchErr.name === 'AbortError') {
                return res.status(504).json({ success: false, message: 'Exophase request timed out.' });
            }
            throw fetchErr;
        } finally {
            clearTimeout(fetchTimeout);
        }

        // Parse achievements using Exophase's confirmed HTML structure.
        // The page has one or more <ul class="achievement|trophy|challenge"> sections,
        // each containing <li> elements with the award data.
        const $ = cheerio.load(html);
        const scraped = [];

        // Primary selectors — match the real Exophase page structure
        $('ul.achievement > li, ul.trophy > li, ul.challenge > li').each((i, el) => {
            const $el = $(el);

            const name = ($el.find('a').first().text() || '').trim();
            if (!name) return; // skip items with no name

            const description = ($el.find('div.award-description p').first().text() || '').trim();
            const iconUrl     = $el.find('img').first().attr('src') || undefined;
            const classesRaw  = ($el.attr('class') || '');
            const isHidden    = classesRaw.split(/\s+/).includes('secret');
            const classes     = classesRaw.toLowerCase();
            const unlocked    = /\b(unlocked|earned|achieved|completed|done)\b/.test(classes);
            const unlockedText = ($el.find('time').first().attr('datetime')
                || $el.find('.date, .earned, .unlock-date, .award-date').first().text()
                || '').trim();

            // Rarity: data-average attribute (0–100 float, percentage of players who earned it)
            const avgRaw = $el.attr('data-average');
            const percent = avgRaw !== undefined ? parseFloat(avgRaw) : undefined;

            // Use 1-based position as the achievement ID (no numeric ID in Exophase HTML)
            let unlockedAt = null;
            if (unlocked) {
                const parsedUnlockDate = unlockedText ? Date.parse(unlockedText) : NaN;
                unlockedAt = Number.isNaN(parsedUnlockDate)
                    ? new Date().toISOString()
                    : new Date(parsedUnlockDate).toISOString();
            }

            const entry = {
                platform,
                gameTitle,
                achievementId: String(i + 1),
                name,
                description,
                unlockedAt,
                source: 'exophase'
            };
            if (iconUrl)              entry.iconUrl  = iconUrl;
            if (isHidden)             entry.hidden   = true;
            if (percent !== undefined && !isNaN(percent)) entry.percent = percent;
            scraped.push(entry);
        });

        if (!scraped.length) {
            return res.status(422).json({
                success: false,
                message: 'No achievements found on the Exophase page. Please verify the URL points to an achievement/trophy list.'
            });
        }

        // Merge into achievements.json (upsert by achievementId + platform + gameTitle)
        const path = `accounts/${usernameLower}/achievements.json`;
        const file = await getFile(path);
        const list = file ? [...file.content] : [];

        let added = 0;
        let updated = 0;
        for (const ach of scraped) {
            const idx = list.findIndex(
                a => a.platform === platform &&
                     (a.gameTitle || '').toLowerCase() === gameTitle.toLowerCase() &&
                     String(a.achievementId) === String(ach.achievementId)
            );
            if (idx !== -1) {
                const existingUnlockedAt = list[idx].unlockedAt || '';
                list[idx] = {
                    ...list[idx],
                    ...ach,
                    unlockedAt: ach.unlockedAt || existingUnlockedAt || '',
                    exophaseProfileId: normalisedProfileId || list[idx].exophaseProfileId || ''
                };
                updated++;
            } else {
                if (normalisedProfileId)
                    ach.exophaseProfileId = normalisedProfileId;
                list.push(ach);
                added++;
            }
        }

        await putFile(
            path,
            list,
            `Sync ${scraped.length} achievements from Exophase for ${gameTitle} (${platform})`,
            file ? file.sha : undefined
        );

        // ── Write achievement template to Games.Database alongside Name.txt ──
        // Path: Data/{platformFolder}/Games/{titleId}/achievements.json
        let gamesDbWritten = false;
        if (titleId && typeof titleId === 'string' && titleId.trim() && octokitGamesDb) {
            const platformFolder = platformToGamesDbFolder(platform);
            if (platformFolder) {
                const safeTitle = titleId.trim();
                // Only allow alphanumeric, underscore, and hyphen to prevent path traversal
                if (/^[a-zA-Z0-9_-]+$/.test(safeTitle)) {
                    const gamesDbPath = `Data/${platformFolder}/Games/${safeTitle}/achievements.json`;
                    // Strip user-specific fields; keep only game-level data
                    const template = scraped.map(a => {
                        const t = { achievementId: a.achievementId, name: a.name, description: a.description };
                        if (a.iconUrl) t.iconUrl = a.iconUrl;
                        return t;
                    });
                    // Sanitise user inputs used in the commit message
                    const safeGameTitle = String(gameTitle).replace(/[\r\n]/g, ' ').slice(0, 80);
                    const safePlatform  = String(platform).replace(/[\r\n]/g, ' ').slice(0, 20);
                    try {
                        const existing = await getGamesDbFile(gamesDbPath);
                        await putGamesDbFile(
                            gamesDbPath,
                            template,
                            `Add achievements for ${safeGameTitle} (${safePlatform}) from Exophase`,
                            existing ? existing.sha : undefined
                        );
                        gamesDbWritten = true;
                    } catch (dbErr) {
                        // Non-fatal: log but don't fail the whole request
                        console.error('sync-exophase: failed to write to Games.Database:', dbErr.message);
                    }
                }
            }
        }

        res.json({
            success: true,
            message: `Synced achievements from Exophase: ${added} added, ${updated} updated.`,
            added,
            updated,
            total: scraped.length,
            gamesDbUpdated: gamesDbWritten,
            warning: hadFragment
                ? 'Exophase URL fragment was ignored during fetch; profile ID must be provided separately.'
                : undefined
        });
    } catch (err) {
        console.error('POST /api/me/achievements/sync-exophase error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── POST /api/admin/scrape-exophase ──────────────────────────────────────────
// Admin-only endpoint: scrape an Exophase achievements page and write the
// template directly to Games.Database (Data/{folder}/Games/{titleId}/achievements.json).
// Authentication: the caller must be the Admin.GameOS account (checked via token).
// Body: { exophaseUrl, platform, gameTitle, titleId }
app.post('/api/admin/scrape-exophase', authenticateToken, async (req, res) => {
    try {
        // Admin-only guard
        if (req.tokenUser.usernameLower !== ADMIN_USERNAME_LOWER) {
            return res.status(403).json({ success: false, message: 'Admin access required.' });
        }

        const { exophaseUrl, platform, gameTitle, titleId } = req.body;

        if (!exophaseUrl || !platform || !gameTitle || !titleId) {
            return res.status(400).json({ success: false, message: 'exophaseUrl, platform, gameTitle, and titleId are required.' });
        }

        // SSRF protection: only allow https://exophase.com URLs
        let parsedUrl;
        try {
            parsedUrl = new URL(exophaseUrl);
        } catch {
            return res.status(400).json({ success: false, message: 'Invalid URL.' });
        }
        if (parsedUrl.protocol !== 'https:' ||
            (parsedUrl.hostname !== 'exophase.com' && parsedUrl.hostname !== 'www.exophase.com')) {
            return res.status(400).json({ success: false, message: 'Only https://exophase.com URLs are allowed.' });
        }

        if (!octokitGamesDb) {
            return res.status(503).json({ success: false, message: 'GAMES_DB_TOKEN is not configured on the server.' });
        }

        const platformFolder = platformToGamesDbFolder(platform);
        if (!platformFolder) {
            return res.status(400).json({ success: false, message: `Unknown platform: ${platform}` });
        }

        const safeTitleId = String(titleId).trim();
        if (!/^[a-zA-Z0-9_-]+$/.test(safeTitleId)) {
            return res.status(400).json({ success: false, message: 'titleId may only contain alphanumeric characters, underscores, and hyphens.' });
        }

        // Fetch the Exophase page
        const controller = new AbortController();
        const fetchTimeout = setTimeout(() => controller.abort(), 15000);
        let html;
        try {
            const response = await fetch(exophaseUrl, {
                signal: controller.signal,
                headers: {
                    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
                    'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
                    'Accept-Language': 'en-US,en;q=0.9'
                }
            });
            if (!response.ok) {
                return res.status(502).json({ success: false, message: `Failed to fetch Exophase page (HTTP ${response.status}).` });
            }
            html = await response.text();
        } catch (fetchErr) {
            if (fetchErr.name === 'AbortError') {
                return res.status(504).json({ success: false, message: 'Exophase request timed out.' });
            }
            throw fetchErr;
        } finally {
            clearTimeout(fetchTimeout);
        }

        // Parse achievements — selector cascade handles both /achievements/ and /trophies/ pages.
        const $ = cheerio.load(html);
        let items = $('ul.achievement > li, ul.trophy > li, ul.challenge > li');
        if (!items.length) items = $('ul.achievements > li, ul.trophies > li, ul.challenges > li');
        if (!items.length) items = $('li[data-average]').filter((_, el) =>
            $(el).find('a, h4, h5, .title, .award-title').length > 0);

        const scraped = [];
        items.each((i, el) => {
            const $el = $(el);
            const name = ($el.find('a').first().text() ||
                          $el.find('h4, h5, .title, .award-title').first().text() || '').trim();
            if (!name) return;

            const description = ($el.find('div.award-description p, .award-description, .description').first().text() || '').trim();
            const rawIconUrl  = $el.find('img').first().attr('src') || undefined;
            // Only accept icon URLs served over HTTPS from exophase.com
            let iconUrl;
            if (rawIconUrl) {
                try {
                    const iconParsed = new URL(rawIconUrl);
                    if (iconParsed.protocol === 'https:' &&
                        (iconParsed.hostname === 'exophase.com' || iconParsed.hostname.endsWith('.exophase.com'))) {
                        iconUrl = rawIconUrl;
                    }
                } catch { /* ignore malformed icon URLs */ }
            }
            const isHidden    = ($el.attr('class') || '').split(/\s+/).includes('secret');
            const avgRaw      = $el.attr('data-average');
            const percent     = avgRaw !== undefined ? parseFloat(avgRaw) : undefined;

            const entry = {
                achievementId: String(i + 1),
                name,
                description
            };
            if (iconUrl)                                  entry.iconUrl = iconUrl;
            if (isHidden)                                 entry.hidden  = true;
            if (percent !== undefined && !isNaN(percent)) entry.percent = percent;
            scraped.push(entry);
        });

        if (!scraped.length) {
            return res.status(422).json({
                success: false,
                message: 'No achievements found on the Exophase page. Please verify the URL points to an achievement/trophy list.'
            });
        }

        // Write achievement template to Games.Database
        const gamesDbPath = `Data/${platformFolder}/Games/${safeTitleId}/achievements.json`;
        const safeGameTitle = String(gameTitle).replace(/[\r\n]/g, ' ').slice(0, 80);
        const safePlatform  = String(platform).replace(/[\r\n]/g, ' ').slice(0, 20);
        const existing = await getGamesDbFile(gamesDbPath);
        await putGamesDbFile(
            gamesDbPath,
            scraped,
            `Add achievements for ${safeGameTitle} (${safePlatform}) from Exophase`,
            existing ? existing.sha : undefined
        );

        // Also update achievementsUrl in the platform's games JSON so the Game.OS
        // modal can fetch achievements without any manual step.
        const achievementsUrl = `https://raw.githubusercontent.com/${GAMES_DB_REPO_OWNER}/${GAMES_DB_REPO_NAME}/main/${gamesDbPath}`;
        const platformFile    = `${platform}.Games.json`;
        try {
            // Use download_url to handle files > 1 MB (e.g. PS3.Games.json is ~1.1 MB).
            const { data: fileMeta } = await octokitGamesDb.repos.getContent({
                owner: GAMES_DB_REPO_OWNER, repo: GAMES_DB_REPO_NAME, path: platformFile
            });
            let platformData;
            if (fileMeta.content) {
                platformData = JSON.parse(Buffer.from(fileMeta.content, 'base64').toString('utf8'));
            } else {
                const dlResp = await fetch(fileMeta.download_url);
                if (!dlResp.ok) throw new Error(`HTTP ${dlResp.status}`);
                platformData = await dlResp.json();
            }

            let gamesArr, topKey = null;
            if (platformData && Array.isArray(platformData.Games)) {
                gamesArr = platformData.Games; topKey = 'Games';
            } else if (platformData && Array.isArray(platformData.games)) {
                gamesArr = platformData.games; topKey = 'games';
            } else if (Array.isArray(platformData)) {
                gamesArr = platformData;
            } else {
                throw new Error('Unexpected platform JSON format');
            }

            const tidLower   = safeTitleId.toLowerCase();
            const titleLower = (gameTitle || '').toLowerCase();
            const toUpdate   = gamesArr.filter(g =>
                String(g.TitleID || g.title_id || g.titleid || '').toLowerCase() === tidLower ||
                (g.Title || g.game_name || g.title || '').toLowerCase() === titleLower
            );
            if (toUpdate.length) {
                toUpdate.forEach(g => { g.achievementsUrl = achievementsUrl; });
                const updated = topKey ? { ...platformData, [topKey]: gamesArr } : gamesArr;
                await putGamesDbFileLarge(
                    platformFile, updated,
                    `Set achievementsUrl for ${safeGameTitle} (${safePlatform})`
                );
            }
        } catch (dbErr) {
            // Non-fatal: achievements.json was already written successfully.
            console.error('scrape-exophase: failed to update achievementsUrl in platform JSON:', dbErr.message);
        }

        res.json({
            success: true,
            message: `Scraped and saved ${scraped.length} achievements to Games.Database.`,
            total: scraped.length,
            path: gamesDbPath,
            achievementsUrl,
            achievements: scraped
        });
    } catch (err) {
        console.error('POST /api/admin/scrape-exophase error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── POST /api/admin/scrape-steam ─────────────────────────────────────────────
// Admin-only endpoint: fetch the achievement schema for a Steam app via the
// Steam Web API and write achievements.json to Games.Database.
// Authentication: the caller must be the Admin.GameOS account (checked via token).
// Body: { appid, platform, gameTitle, titleId }
app.post('/api/admin/scrape-steam', authenticateToken, async (req, res) => {
    try {
        if (req.tokenUser.usernameLower !== ADMIN_USERNAME_LOWER) {
            return res.status(403).json({ success: false, message: 'Admin access required.' });
        }

        const { appid, platform, gameTitle, titleId } = req.body;

        if (!appid || !platform || !gameTitle || !titleId) {
            return res.status(400).json({ success: false, message: 'appid, platform, gameTitle, and titleId are required.' });
        }

        // Validate appid: must be a non-empty string of digits only
        const safeAppId = String(appid).trim();
        if (!/^\d+$/.test(safeAppId)) {
            return res.status(400).json({ success: false, message: 'appid must be a numeric Steam App ID.' });
        }

        if (!octokitGamesDb) {
            return res.status(503).json({ success: false, message: 'GAMES_DB_TOKEN is not configured on the server.' });
        }

        const platformFolder = platformToGamesDbFolder(platform);
        if (!platformFolder) {
            return res.status(400).json({ success: false, message: `Unknown platform: ${platform}` });
        }

        const safeTitleId = String(titleId).trim();
        if (!/^[a-zA-Z0-9_-]+$/.test(safeTitleId)) {
            return res.status(400).json({ success: false, message: 'titleId may only contain alphanumeric characters, underscores, and hyphens.' });
        }

        // Fetch the Steam achievement schema.
        // The key parameter is optional; the endpoint works for many public games without it.
        // When no key is configured Steam frequently returns an empty game object, so we
        // fall back to HTML scraping of public Steam pages (see below).
        const steamUrl = STEAM_API_KEY
            ? `https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key=${encodeURIComponent(STEAM_API_KEY)}&appid=${encodeURIComponent(safeAppId)}&l=en`
            : `https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?appid=${encodeURIComponent(safeAppId)}&l=en`;
        const controller = new AbortController();
        const fetchTimeout = setTimeout(() => controller.abort(), 15000);
        let steamData;
        try {
            const response = await fetch(steamUrl, { signal: controller.signal });
            if (!response.ok) {
                // Non-OK response (e.g. 400, 401, 403) — fall through to HTML scraping below.
                console.warn(`scrape-steam: Steam API returned HTTP ${response.status}, will try HTML fallback.`);
                steamData = null;
            } else {
                steamData = await response.json();
            }
        } catch (fetchErr) {
            if (fetchErr.name === 'AbortError') {
                // Timeout — fall through to HTML scraping below.
                console.warn('scrape-steam: Steam API request timed out, will try HTML fallback.');
                steamData = null;
            } else {
                throw fetchErr;
            }
        } finally {
            clearTimeout(fetchTimeout);
        }

        const rawAchievements =
            steamData &&
            steamData.game &&
            steamData.game.availableGameStats &&
            Array.isArray(steamData.game.availableGameStats.achievements)
                ? steamData.game.availableGameStats.achievements
                : [];

        // ── HTML scraping fallback ────────────────────────────────────────────────
        // When the Steam Web API returns no achievements (most commonly because
        // STEAM_API_KEY is not configured), scrape publicly accessible Steam pages
        // using Cheerio — the same technique used for Exophase scraping.
        // Sources tried in order:
        //   1. Steam Community global achievement stats  (full data: name + description + icon + %)
        //   2. Steam Store app page                      (limited:  name + icon only, ~10 achievements)
        //   3. GetGlobalAchievementPercentagesForApp API (no key needed: internal name + percent only)
        let htmlScraped = [];
        if (!rawAchievements.length) {
            const htmlSources = [
                `https://steamcommunity.com/stats/${safeAppId}/achievements`,
                `https://store.steampowered.com/app/${safeAppId}/`
            ];
            for (const htmlUrl of htmlSources) {
                if (htmlScraped.length) break;
                const hCtrl    = new AbortController();
                const hTimeout = setTimeout(() => hCtrl.abort(), 20000);
                try {
                    const hResp = await fetch(htmlUrl, {
                        signal: hCtrl.signal,
                        headers: {
                            'User-Agent':      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
                            'Accept':          'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
                            'Accept-Language': 'en-US,en;q=0.9',
                            // Bypass Steam age-gate for mature-rated games
                            'Cookie':          'birthtime=631152001; lastagecheckage=1-January-1990; mature_content=1'
                        }
                    });
                    if (!hResp.ok) continue;
                    const html = await hResp.text();
                    const $h   = cheerio.load(html);

                    // ── Steam Community stats page selectors ────────────────────
                    // <div class="achieveRow">
                    //   <div class="achieveImgHolder"><img src="..."></div>
                    //   <div class="achieveTxtHolder">
                    //     <div class="achieveTxt"><h3>Name</h3><h5>Desc</h5></div>
                    //   </div>
                    //   <div class="achievePercent"><div ... style="width:62.7%">...
                    $h('.achieveRow').each((i, el) => {
                        const $el  = $h(el);
                        const name = ($el.find('.achieveTxt h3, .achieveTxtHolder h3').first().text() || '').trim();
                        if (!name) return;
                        const desc    = ($el.find('.achieveTxt h5, .achieveTxtHolder h5').first().text() || '').trim();
                        const rawIcon = $el.find('img').first().attr('src') || undefined;
                        let iconUrl;
                        if (rawIcon) {
                            try {
                                const p = new URL(rawIcon);
                                if (p.protocol === 'https:') iconUrl = rawIcon;
                            } catch { /* skip malformed URLs */ }
                        }
                        const percentStyle = ($el.find('[style*="width"]').first().attr('style') || '');
                        const pMatch  = percentStyle.match(/width\s*:\s*([\d.]+)%/);
                        const percent = pMatch ? parseFloat(pMatch[1]) : undefined;

                        const entry = { achievementId: String(i + 1), name, description: desc };
                        if (iconUrl)                                  entry.iconUrl = iconUrl;
                        if (percent !== undefined && !isNaN(percent)) entry.percent = percent;
                        htmlScraped.push(entry);
                    });

                    // ── Steam Store page fallback selectors ─────────────────────
                    // <img class="achieveImg" src="..." title="Achievement Name">
                    if (!htmlScraped.length) {
                        $h('img.achieveImg[title]').each((i, el) => {
                            const name = ($h(el).attr('title') || '').trim();
                            if (!name) return;
                            const rawIcon = $h(el).attr('src') || undefined;
                            let iconUrl;
                            if (rawIcon) {
                                try {
                                    const p = new URL(rawIcon);
                                    if (p.protocol === 'https:') iconUrl = rawIcon;
                                } catch { /* skip malformed URLs */ }
                            }
                            const entry = { achievementId: String(i + 1), name, description: '' };
                            if (iconUrl) entry.iconUrl = iconUrl;
                            htmlScraped.push(entry);
                        });
                    }
                } catch (htmlErr) {
                    if (htmlErr.name === 'AbortError') {
                        console.warn(`scrape-steam: HTML fallback timed out for ${htmlUrl}`);
                    } else {
                        console.warn(`scrape-steam: HTML fallback failed for ${htmlUrl}:`, htmlErr.message);
                    }
                } finally {
                    clearTimeout(hTimeout);
                }
            }

            // ── GetGlobalAchievementPercentagesForApp fallback ──────────────────
            // This public Steam API endpoint requires no key and returns all
            // achievement internal names with their global completion percentages.
            // Used as a last resort when HTML scraping also yields nothing
            // (e.g. game stats page not publicly accessible).
            if (!htmlScraped.length) {
                const pctUrl    = `https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid=${encodeURIComponent(safeAppId)}`;
                const pCtrl     = new AbortController();
                const pTimeout  = setTimeout(() => pCtrl.abort(), 15000);
                try {
                    const pResp = await fetch(pctUrl, { signal: pCtrl.signal });
                    if (pResp.ok) {
                        const pData = await pResp.json();
                        const pList = pData &&
                            pData.achievementpercentages &&
                            Array.isArray(pData.achievementpercentages.achievements)
                                ? pData.achievementpercentages.achievements
                                : [];
                        pList.forEach((ach, i) => {
                            if (!ach.name) return;
                            // This endpoint returns internal achievement names (e.g. "ACH_KILL_50"),
                            // not human-readable display names.  achievementId uses the internal name
                            // for uniqueness; the display name field also stores it as a fallback
                            // since no display name is available from this endpoint.
                            const entry = {
                                achievementId: String(ach.name),
                                name:          String(ach.name),
                                description:   ''
                            };
                            if (ach.percent !== undefined && !isNaN(ach.percent)) {
                                entry.percent = ach.percent;
                            }
                            htmlScraped.push(entry);
                        });
                    }
                } catch (pctErr) {
                    if (pctErr.name === 'AbortError') {
                        console.warn('scrape-steam: GetGlobalAchievementPercentagesForApp timed out');
                    } else {
                        console.warn('scrape-steam: GetGlobalAchievementPercentagesForApp failed:', pctErr.message);
                    }
                } finally {
                    clearTimeout(pTimeout);
                }
            }
        }

        if (!rawAchievements.length && !htmlScraped.length) {
            return res.status(422).json({
                success: false,
                message: 'No achievements found for this Steam App ID. Verify the appid is correct and the game has achievements. Consider setting STEAM_API_KEY on the server for guaranteed access.'
            });
        }

        // Map to the Games.Database achievements.json format.
        // Use Steam API data when available (includes internal achievement IDs);
        // otherwise use the HTML-scraped data which already uses sequential IDs.
        const usedHtmlFallback = rawAchievements.length === 0;
        const scraped = rawAchievements.length > 0
            ? rawAchievements.map(ach => {
                const entry = {
                    achievementId: String(ach.name || ''),
                    name:          String(ach.displayName || ach.name || ''),
                    description:   String(ach.description || '')
                };
                if (ach.icon)     entry.iconUrl     = String(ach.icon);
                if (ach.icongray) entry.iconUrlGray = String(ach.icongray);
                if (ach.hidden === 1) entry.hidden   = true;
                return entry;
            })
            : htmlScraped;

        // Write achievement template to Games.Database
        const gamesDbPath  = `Data/${platformFolder}/Games/${safeTitleId}/achievements.json`;
        const safeGameTitle = String(gameTitle).replace(/[\r\n]/g, ' ').slice(0, 80);
        const safePlatform  = String(platform).replace(/[\r\n]/g, ' ').slice(0, 20);
        const existing = await getGamesDbFile(gamesDbPath);
        await putGamesDbFile(
            gamesDbPath,
            scraped,
            `Add achievements for ${safeGameTitle} (${safePlatform}) from Steam`,
            existing ? existing.sha : undefined
        );

        // Update achievementsUrl in the platform's games JSON
        const achievementsUrl = `https://raw.githubusercontent.com/${GAMES_DB_REPO_OWNER}/${GAMES_DB_REPO_NAME}/main/${gamesDbPath}`;
        const platformFile    = `${platform}.Games.json`;
        try {
            const { data: fileMeta } = await octokitGamesDb.repos.getContent({
                owner: GAMES_DB_REPO_OWNER, repo: GAMES_DB_REPO_NAME, path: platformFile
            });
            let platformData;
            if (fileMeta.content) {
                platformData = JSON.parse(Buffer.from(fileMeta.content, 'base64').toString('utf8'));
            } else {
                const dlResp = await fetch(fileMeta.download_url);
                if (!dlResp.ok) throw new Error(`HTTP ${dlResp.status}`);
                platformData = await dlResp.json();
            }

            let gamesArr, topKey = null;
            if (platformData && Array.isArray(platformData.Games)) {
                gamesArr = platformData.Games; topKey = 'Games';
            } else if (platformData && Array.isArray(platformData.games)) {
                gamesArr = platformData.games; topKey = 'games';
            } else if (Array.isArray(platformData)) {
                gamesArr = platformData;
            } else {
                throw new Error('Unexpected platform JSON format');
            }

            const tidLower   = safeTitleId.toLowerCase();
            const titleLower = (gameTitle || '').toLowerCase();
            const toUpdate   = gamesArr.filter(g =>
                String(g.TitleID || g.title_id || g.titleid || '').toLowerCase() === tidLower ||
                (g.Title || g.game_name || g.title || '').toLowerCase() === titleLower
            );
            if (toUpdate.length) {
                toUpdate.forEach(g => { g.achievementsUrl = achievementsUrl; });
                const updated = topKey ? { ...platformData, [topKey]: gamesArr } : gamesArr;
                await putGamesDbFileLarge(
                    platformFile, updated,
                    `Set achievementsUrl for ${safeGameTitle} (${safePlatform})`
                );
            }
        } catch (dbErr) {
            // Non-fatal: achievements.json was already written successfully.
            console.error('scrape-steam: failed to update achievementsUrl in platform JSON:', dbErr.message);
        }

        const sourceLabel = usedHtmlFallback ? 'Steam (web scrape)' : 'Steam API';
        res.json({
            success: true,
            message: `Scraped and saved ${scraped.length} achievements to Games.Database from ${sourceLabel}.`,
            total: scraped.length,
            path: gamesDbPath,
            achievementsUrl,
            source: usedHtmlFallback ? 'html' : 'api',
            achievements: scraped
        });
    } catch (err) {
        console.error('POST /api/admin/scrape-steam error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── GET /api/admin/steam-search ───────────────────────────────────────────────
// Admin-only proxy: forwards a search query to the Steam Store search API and
// returns the raw JSON response.  Avoids CORS restrictions in the browser.
app.get('/api/admin/steam-search', authenticateToken, async (req, res) => {
    try {
        if (req.tokenUser.usernameLower !== ADMIN_USERNAME_LOWER) {
            return res.status(403).json({ success: false, message: 'Admin access required.' });
        }
        const query = String(req.query.query || '').trim();
        if (!query) return res.status(400).json({ success: false, message: 'query parameter is required.' });

        const steamUrl = `https://store.steampowered.com/api/storesearch/?term=${encodeURIComponent(query)}&l=en&cc=us`;
        const steamResp = await fetch(steamUrl);
        if (!steamResp.ok) {
            return res.status(502).json({ success: false, message: `Steam API returned HTTP ${steamResp.status}` });
        }
        const data = await steamResp.json();
        res.json(data);
    } catch (err) {
        console.error('GET /api/admin/steam-search error:', err);
        res.status(500).json({ success: false, message: 'Server error fetching Steam search results.' });
    }
});

// ── GET /api/admin/steam-appdetails ──────────────────────────────────────────
// Admin-only proxy: fetches app details from the Steam Store API for a given
// appid and returns the raw JSON response.
app.get('/api/admin/steam-appdetails', authenticateToken, async (req, res) => {
    try {
        if (req.tokenUser.usernameLower !== ADMIN_USERNAME_LOWER) {
            return res.status(403).json({ success: false, message: 'Admin access required.' });
        }
        const appid = String(req.query.appid || '').trim();
        if (!appid || !/^\d+$/.test(appid)) {
            return res.status(400).json({ success: false, message: 'Valid numeric appid parameter is required.' });
        }

        const steamUrl = `https://store.steampowered.com/api/appdetails?appids=${encodeURIComponent(appid)}&l=en`;
        const steamResp = await fetch(steamUrl);
        if (!steamResp.ok) {
            return res.status(502).json({ success: false, message: `Steam API returned HTTP ${steamResp.status}` });
        }
        const data = await steamResp.json();
        res.json(data);
    } catch (err) {
        console.error('GET /api/admin/steam-appdetails error:', err);
        res.status(500).json({ success: false, message: 'Server error fetching Steam app details.' });
    }
});

// ── POST /api/admin/sync-steam-games ─────────────────────────────────────────
// Admin-only endpoint: fetches the full Steam app catalogue, diffs it against
// the existing PC.Games.json in Games.Database, and directly appends any new
// games — exactly the same write path as the manual "Add PC Game" form.
// No GitHub Actions workflow dispatch required.
// Authentication: the caller must be the Admin.GameOS account (checked via token).
app.post('/api/admin/sync-steam-games', authenticateToken, async (req, res) => {
    try {
        if (req.tokenUser.usernameLower !== ADMIN_USERNAME_LOWER) {
            return res.status(403).json({ success: false, message: 'Admin access required.' });
        }

        if (!octokitGamesDb) {
            return res.status(503).json({ success: false, message: 'GAMES_DB_TOKEN is not configured on the server.' });
        }

        // ── 1. Fetch the Steam app list ────────────────────────────────────
        const STEAM_LIST_URL = 'https://api.steampowered.com/ISteamApps/GetAppList/v2/';
        const steamListResp = await fetch(STEAM_LIST_URL);
        if (!steamListResp.ok) {
            return res.status(502).json({ success: false, message: `Failed to fetch Steam app list (HTTP ${steamListResp.status}).` });
        }
        const steamData = await steamListResp.json();
        const allApps = (steamData && steamData.applist && Array.isArray(steamData.applist.apps))
            ? steamData.applist.apps : [];

        // ── 2. Filter to actual games ──────────────────────────────────────
        const SKIP_KEYWORDS = [
            'dedicated server', ' sdk', 'source sdk', 'soundtrack', ' ost',
            'playtest', 'press review', 'linux client', 'winui', 'steamcmd',
            'steam client', 'tool ', ' tool', 'beta test', 'server beta',
            'dev kit', 'devkit',
        ];
        const steamByAppId = {};
        const seenNames = new Set();
        for (const app of allApps) {
            const appid = app.appid;
            const name  = (app.name || '').trim();
            if (!name || appid == null) continue;
            const nameLower = name.toLowerCase();
            if (SKIP_KEYWORDS.some(kw => nameLower.includes(kw))) continue;
            if (seenNames.has(nameLower)) continue;
            seenNames.add(nameLower);
            steamByAppId[appid] = {
                Title:   name,
                TitleID: String(appid),
                appid:   appid,
                image:   `https://cdn.akamai.steamstatic.com/steam/apps/${appid}/header.jpg`,
                stores:  [{ name: 'Steam', url: `https://store.steampowered.com/app/${appid}/` }],
            };
        }

        // ── 3. Read existing PC.Games.json ─────────────────────────────────
        const platformFile = 'PC.Games.json';
        let fileData;
        try {
            const { data: fileMeta } = await octokitGamesDb.repos.getContent({
                owner: GAMES_DB_REPO_OWNER,
                repo:  GAMES_DB_REPO_NAME,
                path:  platformFile
            });
            if (fileMeta.content) {
                fileData = JSON.parse(Buffer.from(fileMeta.content, 'base64').toString('utf8'));
            } else {
                const dlResp = await fetch(fileMeta.download_url);
                if (!dlResp.ok) throw new Error(`Failed to fetch large platform JSON: HTTP ${dlResp.status}`);
                fileData = await dlResp.json();
            }
        } catch (err) {
            if (err.status === 404) {
                fileData = [];
            } else if (err.status === 403 || (err.message && err.message.includes('too large'))) {
                // File too large for Contents API — fetch via raw URL
                const rawUrl = `https://raw.githubusercontent.com/${GAMES_DB_REPO_OWNER}/${GAMES_DB_REPO_NAME}/main/${platformFile}`;
                const rawResp = await fetch(rawUrl);
                if (!rawResp.ok) throw new Error(`Failed to fetch PC.Games.json via raw URL: HTTP ${rawResp.status}`);
                fileData = await rawResp.json();
            } else {
                throw err;
            }
        }

        // ── 4. Normalise the games array ───────────────────────────────────
        let gamesArr;
        let topKey = null;
        let fileMeta = {};
        if (fileData && Array.isArray(fileData.Games)) {
            gamesArr = fileData.Games;  topKey = 'Games';
            fileMeta = Object.fromEntries(Object.entries(fileData).filter(([k]) => k !== 'Games' && k !== 'games'));
        } else if (fileData && Array.isArray(fileData.games)) {
            gamesArr = fileData.games;  topKey = 'games';
            fileMeta = Object.fromEntries(Object.entries(fileData).filter(([k]) => k !== 'Games' && k !== 'games'));
        } else if (Array.isArray(fileData)) {
            gamesArr = fileData;
        } else {
            return res.status(422).json({ success: false, message: 'Unexpected PC.Games.json format.' });
        }

        // ── 5. Diff: find Steam games not already present ──────────────────
        const existingAppIds = new Set();
        const existingTitles = new Set();
        for (const g of gamesArr) {
            if (g.appid != null) existingAppIds.add(Number(g.appid));
            const t = String(g.Title || g.title || '').trim().toLowerCase();
            if (t) existingTitles.add(t);
        }

        const newGames = Object.entries(steamByAppId)
            .filter(([appid, entry]) =>
                !existingAppIds.has(Number(appid)) &&
                !existingTitles.has(entry.Title.trim().toLowerCase()))
            .map(([, entry]) => entry)
            .sort((a, b) => a.appid - b.appid);

        if (!newGames.length) {
            return res.json({ success: true, added: 0, total: gamesArr.length, message: 'PC.Games.json is already up to date — no new Steam games found.' });
        }

        // ── 6. Append new games and write back ─────────────────────────────
        const updatedArr = [...gamesArr, ...newGames];
        const newContent = topKey
            ? { ...fileMeta, Platform: fileMeta.Platform || 'PC', source: fileMeta.source || 'https://api.steampowered.com/ISteamApps/GetAppList/v2/', [topKey]: updatedArr }
            : { Platform: 'PC', source: 'https://api.steampowered.com/ISteamApps/GetAppList/v2/', Games: updatedArr };

        await putGamesDbFileLarge(
            platformFile,
            newContent,
            `Add ${newGames.length} new Steam game(s) to PC.Games.json (total: ${updatedArr.length})`
        );

        res.json({
            success: true,
            added:   newGames.length,
            total:   updatedArr.length,
            message: `✅ Added ${newGames.length} new Steam game(s) to PC.Games.json (${updatedArr.length} total).`,
            sample:  newGames.slice(0, 5).map(g => g.Title),
        });
    } catch (err) {
        console.error('POST /api/admin/sync-steam-games error:', err);
        res.status(500).json({ success: false, message: `Server error: ${err.message}` });
    }
});


// ── downloadAndStoreImage ─────────────────────────────────────────────────────
// Downloads an image from a trusted CDN URL and commits it to the Games.Database
// repository.  Returns the raw GitHub CDN URL of the stored file on success, or
// the original URL if caching fails (non-fatal).
//
// SSRF protection: only HTTPS URLs from a strict allowlist of known image hosts.

const ALLOWED_IMAGE_HOSTS = new Set([
    'media.rawg.io',
    'cdn2.steamgriddb.com',
    'steamcdn-a.akamaihd.net',
    'cdn.cloudflare.steamstatic.com',
    'store.akamai.steamstatic.com',
    'store.steampowered.com',
    'shared.akamai.steamstatic.com',
    'cdn.fastly.steamstatic.com',
    'cdn.theakamaihd.net',
    'images.igdb.com',
    'media.exophase.com',
    'static.exophase.com',
    'img.exophase.com',
    'static-cdn.jtvnw.net',
    'upload.wikimedia.org',
    'image.api.playstation.com',
    'store-images.s-microsoft.com',
    'assets.nintendo.com',
]);

/** Extracts a safe lowercase file extension (jpg/png/gif/webp) from a URL. */
function imageExtFromUrl(url) {
    try {
        const raw = new URL(url).pathname.split('.').pop() || 'jpg';
        return raw.replace(/[^a-z]/gi, '').toLowerCase() || 'jpg';
    } catch { return 'jpg'; }
}

async function downloadAndStoreImage(imageUrl, gamesDbPath) {
    if (!octokitGamesDb || !imageUrl || typeof imageUrl !== 'string') return imageUrl;

    let parsedUrl;
    try { parsedUrl = new URL(imageUrl); } catch { return imageUrl; }

    if (parsedUrl.protocol !== 'https:') return imageUrl;
    // Check direct host match first, then subdomain match (avoids Set→Array conversion on every call)
    const host = parsedUrl.hostname;
    const isAllowed = ALLOWED_IMAGE_HOSTS.has(host) ||
        [...ALLOWED_IMAGE_HOSTS].some(h => host.length > h.length && host.endsWith('.' + h));
    if (!isAllowed) return imageUrl;

    try {
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 15000);
        let imageData;
        try {
            // Use parsedUrl.href (the URL reconstructed from validated parts) rather than
            // the raw imageUrl string so there is no ambiguity about what is being fetched.
            const resp = await fetch(parsedUrl.href, {
                signal:  controller.signal,
                headers: { 'User-Agent': 'GameOS-Bot/1.0' }
            });
            if (!resp.ok) return imageUrl;
            const buf = await resp.arrayBuffer();
            imageData = Buffer.from(buf);
        } finally {
            clearTimeout(timeout);
        }

        let existingSha;
        try {
            const { data } = await octokitGamesDb.repos.getContent({
                owner: GAMES_DB_REPO_OWNER,
                repo:  GAMES_DB_REPO_NAME,
                path:  gamesDbPath
            });
            existingSha = data.sha;
        } catch (err) {
            if (err.status !== 404) throw err;
        }

        const params = {
            owner:   GAMES_DB_REPO_OWNER,
            repo:    GAMES_DB_REPO_NAME,
            path:    gamesDbPath,
            message: `Cache image: ${gamesDbPath}`,
            content: imageData.toString('base64'),
            committer: { name: 'Game OS Bot', email: 'bot@gameos.com' }
        };
        if (existingSha) params.sha = existingSha;

        await octokitGamesDb.repos.createOrUpdateFileContents(params);
        return `https://raw.githubusercontent.com/${GAMES_DB_REPO_OWNER}/${GAMES_DB_REPO_NAME}/main/${gamesDbPath}`;
    } catch (err) {
        console.warn(`downloadAndStoreImage: failed to cache ${imageUrl}: ${err.message}`);
        return imageUrl; // non-fatal — return original URL
    }
}

// ── POST /api/admin/add-game ──────────────────────────────────────────────────
// Admin-only endpoint: add a new game entry to a Games.Database platform JSON.
// Authentication: the caller must be the Admin.GameOS account (checked via token).
// Body: { platform, game (new game object) }
app.post('/api/admin/add-game', authenticateToken, async (req, res) => {
    try {
        // Admin-only guard
        if (req.tokenUser.usernameLower !== ADMIN_USERNAME_LOWER) {
            return res.status(403).json({ success: false, message: 'Admin access required.' });
        }

        const { platform, game } = req.body;

        if (!platform || !game || typeof game !== 'object' || Array.isArray(game)) {
            return res.status(400).json({ success: false, message: 'platform and game (object) are required.' });
        }

        if (!octokitGamesDb) {
            return res.status(503).json({ success: false, message: 'GAMES_DB_TOKEN is not configured on the server.' });
        }

        // Validate that the platform is known
        if (!Object.prototype.hasOwnProperty.call(PLATFORM_TO_GAMES_DB_FOLDER, platform)) {
            return res.status(400).json({ success: false, message: `Unknown platform: ${platform}` });
        }

        const platformFile = `${platform}.Games.json`;
        let fileData;
        try {
            const { data: fileMeta } = await octokitGamesDb.repos.getContent({
                owner: GAMES_DB_REPO_OWNER,
                repo:  GAMES_DB_REPO_NAME,
                path:  platformFile
            });
            if (fileMeta.content) {
                fileData = JSON.parse(Buffer.from(fileMeta.content, 'base64').toString('utf8'));
            } else {
                const dlResp = await fetch(fileMeta.download_url);
                if (!dlResp.ok) throw new Error(`Failed to fetch large platform JSON: HTTP ${dlResp.status}`);
                fileData = await dlResp.json();
            }
        } catch (err) {
            if (err.status === 404) {
                // File doesn't exist yet — start with an empty array
                fileData = [];
            } else {
                throw err;
            }
        }

        // Normalise – some files use { Games: [...] }, some use a bare array
        let gamesArr;
        let topKey = null;
        if (fileData && Array.isArray(fileData.Games)) {
            gamesArr = fileData.Games;  topKey = 'Games';
        } else if (fileData && Array.isArray(fileData.games)) {
            gamesArr = fileData.games;  topKey = 'games';
        } else if (Array.isArray(fileData)) {
            gamesArr = fileData;
        } else {
            return res.status(422).json({ success: false, message: 'Unexpected games JSON format.' });
        }

        // Check for duplicate (case-insensitive title match)
        const newTitle = String(game.Title || game.game_name || game.title || '').toLowerCase();
        const duplicate = gamesArr.some(
            g => (g.Title || g.game_name || g.title || '').toLowerCase() === newTitle
        );
        if (duplicate) {
            return res.status(409).json({ success: false, message: `"${game.Title || game.title}" already exists in ${platformFile}.` });
        }

        gamesArr.push(game);
        const newContent = topKey ? { ...fileData, [topKey]: gamesArr } : { Games: gamesArr };
        const safeTitle    = String(game.Title || game.game_name || game.title || '').replace(/[\r\n]/g, ' ').slice(0, 80);
        const safePlatform = String(platform).replace(/[\r\n]/g, ' ').slice(0, 20);

        await putGamesDbFileLarge(platformFile, newContent, `Add game: ${safeTitle} (${safePlatform})`);

        // Write game data to Data/{platformFolder}/Games/{titleId}/info.json.
        // compile_pc_games.py uses a merge-based approach: it overlays curated
        // info.json fields onto the existing PC.Games.json (which holds the full
        // Steam catalogue) rather than replacing the entire file, so writing
        // info.json for PC games is safe.
        const platformFolder = platformToGamesDbFolder(platform);
        if (platformFolder) {
            const rawId = String(game.TitleID || game.title_id || game.titleid || '').trim();
            const titleIdForPath = rawId && /^[a-zA-Z0-9_-]+$/.test(rawId) ? rawId : null;
            if (titleIdForPath) {
                const infoPath = `Data/${platformFolder}/Games/${titleIdForPath}/info.json`;
                try {
                    const existingInfo = await getGamesDbFile(infoPath);
                    await putGamesDbFile(
                        infoPath,
                        game,
                        `Add game info: ${safeTitle} (${safePlatform})`,
                        existingInfo ? existingInfo.sha : undefined
                    );
                } catch (infoErr) {
                    // Non-fatal: log but don't fail the main add
                    console.error('add-game: failed to write info.json:', infoErr.message);
                }

                // Cache cover/background/screenshots in Games.Database so the app
                // is no longer dependent on external CDNs (see §3 of the feature roadmap).
                try {
                    const imgDir = `Data/${platformFolder}/Games/${titleIdForPath}/images`;
                    if (game.CoverUrl || game.cover_url || game.coverUrl) {
                        const rawUrl = game.CoverUrl || game.cover_url || game.coverUrl;
                        const ext = imageExtFromUrl(rawUrl);
                        const cached = await downloadAndStoreImage(rawUrl, `${imgDir}/cover.${ext}`);
                        if (cached !== rawUrl) { game.CoverUrl = cached; }
                    }
                    if (game.BackgroundUrl || game.background_url || game.backgroundUrl) {
                        const rawUrl = game.BackgroundUrl || game.background_url || game.backgroundUrl;
                        const ext = imageExtFromUrl(rawUrl);
                        const cached = await downloadAndStoreImage(rawUrl, `${imgDir}/background.${ext}`);
                        if (cached !== rawUrl) { game.BackgroundUrl = cached; }
                    }
                    const screenshots = game.Screenshots || game.screenshots || game.screenshotUrls || game.screenshot_urls;
                    if (Array.isArray(screenshots) && screenshots.length) {
                        const cachedScreenshots = [];
                        for (let i = 0; i < screenshots.length; i++) {
                            const rawUrl = screenshots[i];
                            if (!rawUrl || typeof rawUrl !== 'string') { cachedScreenshots.push(rawUrl); continue; }
                            const ext = imageExtFromUrl(rawUrl);
                            cachedScreenshots.push(await downloadAndStoreImage(rawUrl, `${imgDir}/screenshot_${i + 1}.${ext}`));
                        }
                        if (game.Screenshots)     game.Screenshots     = cachedScreenshots;
                        if (game.screenshots)     game.screenshots     = cachedScreenshots;
                        if (game.screenshotUrls)  game.screenshotUrls  = cachedScreenshots;
                        if (game.screenshot_urls) game.screenshot_urls = cachedScreenshots;
                    }
                } catch (imgErr) {
                    // Non-fatal: images are best-effort
                    console.warn('add-game: image caching error:', imgErr.message);
                }
            }
        }

        res.json({ success: true, message: `Game added successfully: ${safeTitle}` });
    } catch (err) {
        console.error('POST /api/admin/add-game error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── POST /api/admin/update-game ───────────────────────────────────────────────
// Admin-only endpoint: update a game entry in a Games.Database platform JSON.
// Authentication: the caller must be the Admin.GameOS account (checked via token).
// Body: { platform, game (updated game object), originalTitle, originalId }
app.post('/api/admin/update-game', authenticateToken, async (req, res) => {
    try {
        // Admin-only guard
        if (req.tokenUser.usernameLower !== ADMIN_USERNAME_LOWER) {
            return res.status(403).json({ success: false, message: 'Admin access required.' });
        }

        const { platform, game, originalTitle, originalId } = req.body;

        if (!platform || !game || typeof game !== 'object' || Array.isArray(game)) {
            return res.status(400).json({ success: false, message: 'platform and game (object) are required.' });
        }
        if (!originalTitle && !originalId) {
            return res.status(400).json({ success: false, message: 'At least one of originalTitle or originalId is required.' });
        }

        if (!octokitGamesDb) {
            return res.status(503).json({ success: false, message: 'GAMES_DB_TOKEN is not configured on the server.' });
        }

        // Validate that the platform is known
        if (!Object.prototype.hasOwnProperty.call(PLATFORM_TO_GAMES_DB_FOLDER, platform)) {
            return res.status(400).json({ success: false, message: `Unknown platform: ${platform}` });
        }

        // Fetch the current platform JSON via the authenticated GitHub Contents API so we
        // always read the latest committed version.  The CDN-cached raw URL can lag behind
        // by several minutes, causing a second save to overwrite fields set by a recent
        // first save that is still propagating through the CDN cache.
        const platformFile = `${platform}.Games.json`;
        let fileData;
        try {
            const { data: fileMeta } = await octokitGamesDb.repos.getContent({
                owner: GAMES_DB_REPO_OWNER,
                repo:  GAMES_DB_REPO_NAME,
                path:  platformFile
            });
            if (fileMeta.content) {
                // Content returned inline (files < 1 MB)
                fileData = JSON.parse(Buffer.from(fileMeta.content, 'base64').toString('utf8'));
            } else {
                // File > 1 MB – download_url points at the current blob (no CDN staleness)
                const dlResp = await fetch(fileMeta.download_url);
                if (!dlResp.ok) throw new Error(`Failed to fetch large platform JSON: HTTP ${dlResp.status}`);
                fileData = await dlResp.json();
            }
        } catch (err) {
            if (err.status === 404) {
                return res.status(404).json({ success: false, message: `Platform file not found: ${platformFile}` });
            }
            throw err;
        }

        // Normalise – some files use { Games: [...] }, some use a bare array
        let gamesArr;
        let topKey = null;
        if (fileData && Array.isArray(fileData.Games)) {
            gamesArr = fileData.Games;  topKey = 'Games';
        } else if (fileData && Array.isArray(fileData.games)) {
            gamesArr = fileData.games;  topKey = 'games';
        } else if (Array.isArray(fileData)) {
            gamesArr = fileData;
        } else {
            return res.status(422).json({ success: false, message: 'Unexpected games JSON format.' });
        }

        // Locate the game by original title or title ID
        const origTitleLower = String(originalTitle || '').toLowerCase();
        const origIdStr      = String(originalId    || '');

        const idx = gamesArr.findIndex(g => {
            const gt  = (g.Title || g.game_name || g.title || '').toLowerCase();
            const gid = String(g.TitleID || g.title_id || g.titleid || g.id || (g.appid != null ? g.appid : ''));
            return (origTitleLower && gt === origTitleLower) || (origIdStr && gid === origIdStr);
        });

        if (idx === -1) {
            return res.status(404).json({ success: false, message: 'Game not found in database.' });
        }

        // Merge the incoming fields over the existing entry (preserves any fields not
        // touched by the edit form – e.g. fields added by earlier saves or other tools).
        gamesArr[idx] = { ...gamesArr[idx], ...game };
        const newContent = topKey ? { ...fileData, [topKey]: gamesArr } : gamesArr;
        const safeTitle    = String(game.Title || game.game_name || game.title || '').replace(/[\r\n]/g, ' ').slice(0, 80);
        const safePlatform = String(platform).replace(/[\r\n]/g, ' ').slice(0, 20);

        await putGamesDbFileLarge(platformFile, newContent, `Update game: ${safeTitle} (${safePlatform})`);

        // Write game data to Data/{platformFolder}/Games/{titleId}/info.json.
        // compile_pc_games.py uses a merge-based approach: it overlays curated
        // info.json fields onto the existing PC.Games.json (which holds the full
        // Steam catalogue) rather than replacing the entire file, so writing
        // info.json for PC games is safe.
        const platformFolder = platformToGamesDbFolder(platform);
        if (platformFolder) {
            const rawId = String(game.TitleID || game.title_id || game.titleid || '').trim();
            const titleIdForPath = rawId && /^[a-zA-Z0-9_-]+$/.test(rawId) ? rawId : null;
            if (titleIdForPath) {
                const infoPath = `Data/${platformFolder}/Games/${titleIdForPath}/info.json`;
                try {
                    // getGamesDbFile returns null for 404 (file not yet created) — passing
                    // undefined as sha to putGamesDbFile correctly triggers a new-file create.
                    const existingInfo = await getGamesDbFile(infoPath);
                    await putGamesDbFile(
                        infoPath,
                        gamesArr[idx],
                        `Update game info: ${safeTitle} (${safePlatform})`,
                        existingInfo ? existingInfo.sha : undefined
                    );
                } catch (infoErr) {
                    // Non-fatal: log but don't fail the main update
                    console.error('update-game: failed to write info.json:', infoErr.message);
                }

                // Cache cover/background/screenshots in Games.Database (§3 of feature roadmap)
                try {
                    const imgDir = `Data/${platformFolder}/Games/${titleIdForPath}/images`;
                    const merged = gamesArr[idx];
                    if (merged.CoverUrl || merged.cover_url || merged.coverUrl) {
                        const rawUrl = merged.CoverUrl || merged.cover_url || merged.coverUrl;
                        const ext = imageExtFromUrl(rawUrl);
                        const cached = await downloadAndStoreImage(rawUrl, `${imgDir}/cover.${ext}`);
                        if (cached !== rawUrl) { merged.CoverUrl = cached; gamesArr[idx].CoverUrl = cached; }
                    }
                    if (merged.BackgroundUrl || merged.background_url || merged.backgroundUrl) {
                        const rawUrl = merged.BackgroundUrl || merged.background_url || merged.backgroundUrl;
                        const ext = imageExtFromUrl(rawUrl);
                        const cached = await downloadAndStoreImage(rawUrl, `${imgDir}/background.${ext}`);
                        if (cached !== rawUrl) { merged.BackgroundUrl = cached; gamesArr[idx].BackgroundUrl = cached; }
                    }
                    const screenshots = merged.Screenshots || merged.screenshots || merged.screenshotUrls || merged.screenshot_urls;
                    if (Array.isArray(screenshots) && screenshots.length) {
                        const cachedScreenshots = [];
                        for (let i = 0; i < screenshots.length; i++) {
                            const rawUrl = screenshots[i];
                            if (!rawUrl || typeof rawUrl !== 'string') { cachedScreenshots.push(rawUrl); continue; }
                            const ext = imageExtFromUrl(rawUrl);
                            cachedScreenshots.push(await downloadAndStoreImage(rawUrl, `${imgDir}/screenshot_${i + 1}.${ext}`));
                        }
                        if (merged.Screenshots)     { merged.Screenshots     = cachedScreenshots; gamesArr[idx].Screenshots     = cachedScreenshots; }
                        if (merged.screenshots)     { merged.screenshots     = cachedScreenshots; gamesArr[idx].screenshots     = cachedScreenshots; }
                        if (merged.screenshotUrls)  { merged.screenshotUrls  = cachedScreenshots; gamesArr[idx].screenshotUrls  = cachedScreenshots; }
                        if (merged.screenshot_urls) { merged.screenshot_urls = cachedScreenshots; gamesArr[idx].screenshot_urls = cachedScreenshots; }
                    }
                } catch (imgErr) {
                    // Non-fatal: images are best-effort
                    console.warn('update-game: image caching error:', imgErr.message);
                }
            }
        }

        res.json({ success: true, message: `Game updated successfully: ${safeTitle}` });
    } catch (err) {
        console.error('POST /api/admin/update-game error:', err);
        res.status(500).json({ success: false, message: 'Server error.' });
    }
});

// ── Admin account initialisation ──────────────────────────────────────────────
/**
 * Creates the Admin.GameOS account on first startup if it doesn't already exist.
 * The initial password is read from ADMIN_GAMEOS_PASSWORD env var (defaults to "GameOS2026").
 * Change the password after first login via the account settings page.
 */
async function initAdminAccount() {
    if (!REPO_OWNER || !REPO_NAME) return;
    try {
        const existing = await getFile(`accounts/${ADMIN_USERNAME_LOWER}/profile.json`);
        if (existing) return; // already initialised

        const adminPassword = process.env.ADMIN_GAMEOS_PASSWORD || 'GameOS2026';
        if (!process.env.ADMIN_GAMEOS_PASSWORD) {
            console.warn('⚠️  ADMIN_GAMEOS_PASSWORD is not set. Using the default password "GameOS2026". Set this env var and change the admin password after first login.');
        }
        const passwordHash  = await bcrypt.hash(adminPassword, BCRYPT_ROUNDS);

        await putFile(
            `accounts/${ADMIN_USERNAME_LOWER}/profile.json`,
            {
                username:      ADMIN_USERNAME,
                email:         ADMIN_EMAIL,
                password_hash: passwordHash,
                created_at:    new Date().toISOString(),
                is_admin:      true
            },
            `Initialize admin account: ${ADMIN_USERNAME}`
        );

        // Add admin email to the index
        const emailIndexFile = await getFile('accounts/email-index.json');
        const emailMap       = emailIndexFile ? { ...emailIndexFile.content } : {};
        if (!emailMap[ADMIN_EMAIL]) {
            emailMap[ADMIN_EMAIL] = ADMIN_USERNAME_LOWER;
            await putFile(
                'accounts/email-index.json',
                emailMap,
                `Add email index for admin: ${ADMIN_USERNAME}`,
                emailIndexFile ? emailIndexFile.sha : undefined
            );
        }

        console.log(`✅ Admin account "${ADMIN_USERNAME}" created successfully.`);
    } catch (err) {
        console.error('⚠️  Failed to initialize admin account:', err.message);
    }
}

// ── Start server ──────────────────────────────────────────────────────────────
const PORT = process.env.PORT || 3000;
app.listen(PORT, () => {
    console.log(`✅ Game.OS Backend running on port ${PORT}`);
    console.log(`   Data repository: ${REPO_OWNER}/${REPO_NAME}`);
    initAdminAccount();
});
