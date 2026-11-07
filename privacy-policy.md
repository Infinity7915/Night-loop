# Night-loop Privacy Policy

**Version:** 2026-11-07
**Effective date:** November 7, 2026

This policy explains what personal data Night-loop collects, why, how long it is kept, who else sees it, and
what you can do about it. It covers the Night-loop desktop app for Windows (the "App") and the Night-loop account
server at `api.night-loop.win` (the "Service"). It is part of the [Terms of Service](terms-of-service.md).

## 1. Who is responsible for your data

Night-loop is run by **Cryo** (the "operator", "we", "us"), who decides how and why your data is used (the "data
controller" under laws such as the GDPR).

**Contact:** the [contact page](https://api.night-loop.win/contact) (no account needed). Use it for any question
about your data or to use any of the rights in section 8. Messages go straight to the operator, and answers come by
email. Please don't post personal details in a public GitHub issue.

## 2. Data that stays on your PC

These are never sent to the Service:

- **No input recording.** The App doesn't record your keyboard, mouse or scroll wheel. Macros are built from
  blocks in the editor. (Versions before 0.10.0 could record input on your PC while you pressed Record; blocks
  recorded then stay in your macros and still play.)
- **The screen, when a macro looks at it.** Detect blocks (Wait for text, Wait for image, Find & click and
  others) read pixels or text from your screen on your PC. Nothing from the screen is uploaded, except a picture
  you choose to save inside a macro.
- **Screenshots** taken by the Screenshot block, saved to your Pictures folder.
- **Local settings** in `%LOCALAPPDATA%\NightLoop`, and your sign-in token, encrypted with Windows DPAPI.
- **Error logs** in `%LOCALAPPDATA%\NightLoop\logs`. They stay on your PC unless you send them to us.

## 3. Data on the Service

| Data | Why we use it | How it's protected | Kept until |
| --- | --- | --- | --- |
| Email address | Sign-in, codes, security notices | Encrypted (AES-256-GCM); looked up by a keyed hash | Account deleted + 30 days |
| Password | Sign-in | Never stored; only a one-way Argon2id hash | Changed, or account deleted + 30 days |
| Username and user number | Shown in the App | Plain text (it's a display name) | Account deleted + 30 days |
| Macros and macro groups (names, blocks, saved pictures and text) | So your macros follow your account | Encrypted (AES-256-GCM) | You delete them, or account deleted + 30 days |
| Macro settings (hotkeys, target game, trigger mode, block count) | Listing your macros | Plain text | Same as the macro |
| PC name (for example "Windows 11 · DESKTOP-7Q2") | Device list and "new sign-in" emails | Encrypted (AES-256-GCM) | Account deleted + 30 days |
| A hash of your PC's hardware ID and a random install ID | Recognising your PC for two-step sign-in | One-way hash only | Account deleted + 30 days |
| Session and "trust this PC" tokens, email codes | Keeping you signed in, confirming changes | One-way hashes only | Codes: 1 day after they expire. Sessions: 30 days after they end |
| IP address | Rate limits and the security log | Hashed for rate limits; shortened (last part removed) in the security log | Rate limits: minutes to a day. Security log: 180 days |
| Security log (sign-ins and changes to your account) | Protecting your account and the Service | Never contains passwords, codes or tokens | 180 days |
| Date you accepted the Terms, and their version | Proof of agreement | Plain text | Account deleted + 30 days |
| Date your account was created | Usage statistics | Plain text | Account deleted + 30 days |
| Messages you send through the contact page (name, email, message, our answer) | Answering you, and a record of privacy and legal requests | Encrypted (AES-256-GCM) | 2 years |
| Total time the App has been open and signed in (seconds) | Usage statistics | Plain text | Account deleted + 30 days |

The usage time is added up from the App's check-in with the Service, about once a minute while it is open and
signed in (also while it is hidden in the system tray).

Under the [Terms of Service](terms-of-service.md) (section 5), macros are Night-loop's property. That doesn't
change your rights in this policy: if a macro holds personal data about you, such as a picture of your screen
or text you typed, you can still ask about it, have it corrected, or have it deleted.

## 4. Why we're allowed to use it (legal bases)

- **To provide the Service you signed up for** (performance of a contract): your account, email, password hash,
  macros, devices and sessions.
- **Legitimate interests**: keeping the Service and accounts secure (security log, rate limits, IP handling,
  new-device emails) and understanding how much Night-loop is used (account creation date and usage time). We
  keep these to the minimum shown above.
- **Legal obligations**: keeping a record of consent and answering lawful requests.

We don't use your data for advertising, don't sell it, don't build profiles of you and don't make automated
decisions about you.

## 5. Who else sees it

Only the services that run Night-loop for us, and only for that purpose:

| Service | What it does | What it sees |
| --- | --- | --- |
| Cloudflare | Runs the Service and its address | Your IP address and requests, while they pass through |
| Neon | Hosts the database | The stored data in section 3 (sensitive fields are encrypted before they reach it) |
| Resend | Sends Night-loop's emails | Your email address and the email we send you |
| GitHub | Hosts the App's downloads, updates and announcements, and the Service's encrypted backups | Your IP address when the App checks for updates; backups are encrypted before upload |
| Have I Been Pwned (if enabled) | Checks a new password against known leaks | Only the first 5 characters of a hash of your password |

We share data with others only if the law requires it (for example a valid court order), or to protect people
or the Service from serious harm.

## 6. Where it's stored

These providers may process data in the United States and other countries. Where data leaves the EU, the UK or
your country, we rely on the providers' standard contractual clauses or similar safeguards.

## 7. Security and backups

Connections use HTTPS. Emails, PC names and macros are encrypted in the database, and passwords, tokens and
codes are stored only as one-way hashes. The database is backed up once a day; backups are encrypted and kept for
14 days, then deleted automatically. No system is perfectly secure. If a breach affects your data, we'll tell you
and the authorities as the law requires.

## 8. Your rights

Depending on where you live (for example under the GDPR, the UK GDPR or the CCPA), you may have the right to:

- **access** your data and **get a copy** of it: Settings › Account › Download my data, or write to us;
- **correct** it: change your email and username in the App, or write to us;
- **delete** it: Settings › Account › Delete account. Your account is disabled at once and deleted for good after
  30 days. Copies in backups disappear within 14 days after that;
- **object to** or **restrict** some uses, such as usage statistics;
- **withdraw consent** where we rely on it, and **complain** to your local data protection authority.

Use the [contact page](https://api.night-loop.win/contact) for anything the App can't do for you. We answer
within one month (45 days under the CCPA) and may ask you to confirm the request from your account's email
address. We never charge for this.

## 9. Children

Night-loop is not for children under 13. In countries that require a higher age for consenting to data use
without a parent (up to 16 in some EU countries), users under that age need a parent's or guardian's permission.
If you think a child under 13 has an account, tell us through the contact page and we'll delete it.

## 10. Cookies

The App and the Service don't use cookies, ads or trackers. (The only cookie is a sign-in cookie on the
operator's own admin page, which ordinary users never visit.)

## 11. Changes to this policy

We'll update this page when Night-loop's data use changes, with a new version and date at the top. For significant
changes we'll tell you in advance through the App's announcements. Every past version is in this repository's
commit history.
