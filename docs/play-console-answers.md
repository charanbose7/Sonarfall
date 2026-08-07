# Sonarfall — Play Console declaration answer sheet

Every answer below is derived from the actual codebase, verified on 8 Aug 2026:
no network code, no ad/analytics SDKs, no device identifiers, `VIBRATE` as the only
permission, and all state in local `PlayerPrefs`.

---

## Content rating (IARC questionnaire)

**Step 1 — Category** *(already filled in the console)*
- Email address: `charan@vortexforgestudios.com`
- Category: **Game**
- Terms and conditions: **left unticked — you need to accept the IARC Terms of Use yourself**

**Step 2 — Questionnaire.** Answer **No** to every question. Sonarfall has no
representational content at all: the entire game is abstract geometry on black.

| Section | Answer |
|---|---|
| Violence (realistic, fantasy, blood, gore) | No |
| Sexuality / nudity | No |
| Language (profanity, crude humour) | No |
| Controlled substances (drugs, alcohol, tobacco) | No |
| Gambling (simulated or real) | No |
| Horror / fear themes | No |
| Discrimination / hate | No |
| Crime / criminal activity | No |
| **Miscellaneous** | |
| Does the app share the user's location? | **No** |
| Does the app allow users to interact or exchange content? | **No** |
| Does the app allow purchase of digital goods? | **No** |
| Does the app contain user-generated content? | **No** |
| Does the app share personal info with third parties? | **No** |
| Is this a web browser / search engine? | **No** |

**Expected outcome:** ESRB *Everyone*, PEGI *3*, USK *0*, IARC *3+*.

> One judgement call worth knowing: some developers tick "horror/fear themes" for a
> game set in total darkness. Sonarfall has no threat, no jump scares, no creatures —
> the dark is a *visibility* mechanic, not an atmosphere of dread. **No** is the
> honest answer, and it keeps the rating at Everyone.

---

## Data safety

**Step 1 — Data collection and security**
- Does your app collect or share any of the required user data types? → **No**
- *(Every subsequent data-type page disappears once you answer No.)*
- Is all of the user data collected by your app encrypted in transit? → **N/A** (no data collected)
- Do you provide a way for users to request that their data be deleted? → **N/A** (no data collected)

**Why this is accurate:** `SaveData.cs` writes 14 `PlayerPrefs` keys — current level,
day streak, daily-challenge state, sound/haptics toggles, and tutorial-seen flags.
Play's Data safety form explicitly **excludes** data that never leaves the device.
Nothing is transmitted anywhere; the app has no internet permission.

---

## Still outstanding

| Item | Blocked on |
|---|---|
| Set privacy policy | The policy URL must be live first — see below |
| Content rating | You accepting the IARC Terms of Use, then the answers above |
| Data safety | Just needs running through with the answers above |
| Store listing | **Feature graphic (1024 × 500) does not exist yet** |

### Privacy policy — two steps on your side

1. Push `docs/privacy-policy.html` to `charanbose7/Sonarfall`.
2. Repo **Settings → Pages →** serve from `main` branch, `/docs` folder.

That gives you `https://charanbose7.github.io/Sonarfall/privacy-policy.html`, which
is what goes in the "Set privacy policy" field. I can't change repo settings for you.

### App icon caveat

`Assets/Sprites/Echo Maze Logo.png` is 512×512 but **RGBA**. Play rejects icons with
transparency — the alpha channel needs flattening onto an opaque background before
upload.
