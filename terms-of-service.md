# Night-loop Terms of Service

**Version:** 2026-11-04
**Effective date:** November 4, 2026

These Terms of Service ("Terms") are an agreement between you and the operator of Night-loop ("Night-loop",
"we", "us" or "our"). They cover the Night-loop desktop application for Windows (the "App"), the Night-loop
account server at `api.night-loop.win` (the "Service"), and this GitHub repository, together the "Night-loop
software and services".

By ticking the box on the **Create account** screen, creating an account, or using the App or the Service, you
agree to these Terms. If you don't agree, don't create an account or use Night-loop.

## 1. Changes to these Terms

We may update these Terms from time to time, for example when Night-loop gets new features, when the law
changes, or to make them clearer. The current version is always on this page, with its version and effective
date at the top. The full history of every change is kept in this repository's commit history.

If a change is significant, we will try to tell you in advance, for example through the announcements on the
App's Home screen or the release notes. By continuing to use Night-loop after a change takes effect, you accept
the updated Terms. If you don't accept them, stop using Night-loop and delete your account (Settings › Account ›
Delete account).

## 2. Who can use Night-loop

- You must be at least **13 years old**. In countries where a higher age is needed to agree to the processing of
  personal data without a parent's consent (up to 16 in some EU countries), you must be that age or have your
  parent's or guardian's permission.
- You must be able to form a binding agreement where you live, and not be barred from using the software by law.
- If you use Night-loop on behalf of an organization, you confirm you're allowed to accept these Terms for it.

## 3. Your account

- One account per person. Give a real email address you can read; it's used for sign-in codes and security
  notices.
- Keep your password secret and your email account secure. You're responsible for what happens under your
  account. Tell us straight away if you think someone else has accessed it.
- An account can be signed in on one PC at a time. Signing in on another PC signs the first one out.
- You can change your username up to the limit shown in the App, and delete your account at any time.

## 4. What Night-loop records and stores

Night-loop is built to collect as little as it can. In short:

**On your PC only (never sent to the Service):**

- **Recordings while you record.** Only while you press **Record**, the App captures your keyboard keys, mouse
  buttons, mouse movement (if "Record mouse movement" is on) and scroll wheel, and turns them into macro blocks.
  It does not record anything when you aren't recording.
- **The screen, when a macro looks at it.** Detect blocks (Wait for text, Wait for image, Image gone, Pixel gone,
  Find & click and others) and screen tools read pixels or text from your screen on your PC. Screen pictures
  and text read from the screen are not uploaded, except a picture you choose to save inside a macro.
- **Screenshots** taken by the Screenshot block, saved to your Pictures folder.
- **Local settings** (theme, target game, hotkeys, tray and editor options) in `%LOCALAPPDATA%\NightLoop`, and
  your sign-in token, encrypted with Windows DPAPI.
- **Error logs** in `%LOCALAPPDATA%\NightLoop\logs`, which stay on your PC unless you send them to us.

**On the Service (our server):**

| What | Why | How it's protected |
| --- | --- | --- |
| Email address | Sign-in, codes, security notices | Encrypted (AES-256-GCM); looked up by a keyed hash |
| Password | Sign-in | Never stored; only a one-way Argon2id hash |
| Username and user number | Shown in the App | Plain text (it's a display name) |
| Your macros and macro groups (names, blocks, saved pictures) | So your macros follow your account | Encrypted (AES-256-GCM) |
| Macro settings (hotkeys, target game, trigger mode, block count) | Listing your macros | Plain text |
| PC name (e.g. "Windows 11 · DESKTOP-7Q2") | Device list and "new sign-in" emails | Encrypted (AES-256-GCM) |
| A hash of your PC's hardware ID and a random install ID | Recognising your PC for two-factor sign-in | One-way hash only |
| Session and "trust this PC" tokens, email codes | Keeping you signed in, confirming changes | One-way hashes only |
| IP address | Rate limits and the security log | Hashed for rate limits; shortened (last part removed) in the security log |
| Security log (sign-ins, changes to your account) | Protecting your account | Kept 180 days; never contains passwords, codes or tokens |
| Date you accepted these Terms, and their version | Proof of agreement | Plain text |
| **Date your account was created** | Usage statistics | Plain text |
| **Total time Night-loop has been open and signed in** (in seconds) | Usage statistics | Plain text |

The total time is added up from the App's regular check-in with the Service (about once a minute while it's open
and signed in, including while it's hidden in the system tray).

**Third parties.** We don't sell your data and don't use it for advertising. The only outside services involved
are our hosting providers (Cloudflare and Neon, which run the Service and its database), our email provider
(which sees your address and the email we send you), GitHub (which hosts the App's downloads, updates and
announcements; GitHub sees your IP address when the App checks for updates), and, if enabled, Have I Been Pwned's
password check (which only ever sees the first 5 characters of a hash of your password).

**Security.** Data on the Service is protected as described above, and all connections use HTTPS. No system is
perfectly secure. Encryption protects a copy of the database, but someone who took over the running server
itself could read what the server can read. We will tell affected users about a breach as the law requires.

**Your rights.** Depending on where you live (for example under the GDPR or the CCPA), you may have rights to
access, correct, delete or get a copy of your personal data, and to object to some uses of it. Delete your
account in Settings; for anything else, contact us (section 14). After you delete your account, it's disabled
straight away and deleted for good after 30 days.

## 5. Your macros and content

- Your macros are yours. You give us only the permission we need to store them, sync them to your account and
  show them back to you in the App.
- Macros are kept on the Service and are not exported or shared by Night-loop.
- You're responsible for what your macros do, and for having the right to use any picture or text you put in
  them.

## 6. Acceptable use

When you use Night-loop, you agree **not** to:

- break any law, or use Night-loop to harm, harass, defraud or spy on anyone;
- use it in a game, program or website whose rules forbid macros, automation or third-party input tools
  (see section 7);
- attack, overload, probe or reverse-engineer the Service, get around its limits or security, or access
  another person's account or data;
- create accounts automatically, share or sell accounts, or impersonate someone;
- use the Service to store or spread malware or illegal content.

We may limit, suspend or close accounts that break these rules (section 11).

## 7. Games and other software

Night-loop sends keyboard and mouse input and reads the screen, the same way a person would. Many games and
online services have rules about macros, automation or "botting", and some ban accounts for it. **It's your
responsibility to check and follow the rules of every game or program you use Night-loop with.** Night-loop is
not affiliated with, endorsed by or sponsored by Roblox, Fortnite, Minecraft or any other game or company
named in the App; their names are used only to identify them. We aren't responsible for any action another
service takes against your account there.

## 8. Updates, availability and changes to the service

- The App updates itself from this GitHub repository. Older versions may stop working with the Service once a
  newer version is required.
- We may change, add or remove features, or limit how many macros or groups an account can have.
- We try to keep the Service running but don't promise it will always be available, fast or error-free, and we
  may stop it with reasonable notice where we can.
- Paid plans may be offered in the future. If they are, their price and terms will be shown before you pay,
  and they'll be added to these Terms.

## 9. Software license

The App's source code in this repository is released under the [MIT License](LICENSE). That license covers the
code. These Terms cover your account and your use of the Service. The Night-loop name and logo are not
licensed for use in other products.

## 10. Feedback

If you send ideas or feedback, we may use them without owing you anything.

## 11. Suspension and termination

You can stop using Night-loop and delete your account at any time. We may suspend or close your account, or
refuse service, if you break these Terms, if the law requires it, or to protect the Service or other users.
Where reasonable, we'll tell you why. Sections 5, 7, 12, 13 and 14 continue after your account ends.

## 12. Disclaimer of warranties

Night-loop is provided **"as is" and "as available"**, without warranties of any kind, express or implied,
including merchantability, fitness for a particular purpose, non-infringement and that it will be secure,
uninterrupted or free of errors. Macros send real input to your PC; a macro can click, type or close things you
didn't intend. Test your macros carefully and use the emergency stop (Ctrl+Alt+End). Some places don't allow
these exclusions, so some of them may not apply to you.

## 13. Limitation of liability

To the fullest extent the law allows, the operator of Night-loop and its contributors are not liable for any
indirect, incidental, special, consequential or punitive damages, or for lost data, profits, game items or
accounts, arising from your use of or inability to use Night-loop, even if told such damage was possible. Our
total liability for any claim about Night-loop is limited to the greater of the amount you paid us in the 12
months before the claim, or USD $10. Nothing in these Terms limits liability that can't be limited by law, or
your rights as a consumer under the law where you live.

You agree to cover claims brought against us by others that result from your breaking these Terms or the law
while using Night-loop, to the extent the law allows.

## 14. General terms and contact

- **Whole agreement.** These Terms are the whole agreement between you and us about Night-loop and replace any
  earlier version.
- **Severability.** If a part of these Terms can't be enforced, the rest still applies.
- **No waiver.** Not enforcing a part of these Terms isn't giving it up.
- **Assignment.** You can't transfer these Terms to someone else. We may transfer them if Night-loop changes
  hands, and your data stays protected as described here.
- **Governing law.** These Terms are governed by the laws of the place where the operator of Night-loop is based,
  without regard to conflict-of-law rules, except where the law of your country gives you consumer protections
  that can't be overridden.
- **Contact.** Questions about these Terms or your data: open an issue in this repository
  ([github.com/Infinity7915/Night-loop/issues](https://github.com/Infinity7915/Night-loop/issues)). Please don't
  post personal details in a public issue; ask for a private contact there instead.
