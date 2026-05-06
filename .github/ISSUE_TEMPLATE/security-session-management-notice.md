## Description

### Context:
Storing sensitive data, such as authentication tokens, in `localStorage` or `sessionStorage` introduces significant security risks, including susceptibility to Cross-Site Scripting (XSS) attacks. Best practices for securely managing user sessions should replace these patterns.

## Changes Required:

1. **Adopt HTTP-Only Secure Cookies**: Replace any sessions stored in `localStorage` or `sessionStorage` with HTTP-Only cookies that are inaccessible to JavaScript, mitigating XSS.
2. **Use `Secure` and `SameSite` Attributes**:
     - Enforce secure transmission through HTTPS.
     - Harden cross-site CSRF protection.
3. **Switch Tokens to In-Memory Storage**:
    - Particularly for SPAs, balance refresh strategy lossability of `access_tokens` while ensuring process-wide isolation.
4. **Ensure Proper Input Validation and Sanitization**: Thoroughly validate and sanitize user inputs to prevent injection attacks.

## Guidance:

### Why is `localStorage`/`sessionStorage` insecure?
These storage mechanisms are easily accessible to all scripts running on the origin. If an attacker injects malicious scripts (via XSS vulnerabilities), these scripts can retrieve sensitive tokens or identifiers stored in `localStorage`/`sessionStorage`:

```javascript
// Example of how a malicious script may retrieve stored data:
const token = localStorage.getItem('auth_token');
console.log(token);
```

### Alternatives for SPAs:
While storing tokens in memory removes persistence through refreshes, it ensures tokens are cleared when the window is closed. This trade-off improves security by avoiding exposure in persistent client-side storage.