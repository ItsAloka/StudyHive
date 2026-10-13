# Development accounts

Four accounts, one per role, for clicking through the app locally. **These already exist** — the API
seeds them into the database automatically on startup in Development. Nothing needs creating.

All four verified by a real login against the running API: HTTP 200, correct role, token returned.

> **Development only.** These work solely against your local database. They are already committed in
> `api/src/StudyHive.Api/appsettings.Development.json`, so this file is convenience, not new
> exposure. The API refuses to start outside Development without real configuration, so these cannot
> reach anything deployed. Never reuse this password anywhere real.

---

## The password (same for all four)

```
Dev-Only-Passw0rd!
```

**Copy it. Do not retype it.** `Passw0rd` uses a **zero**, not a capital O, and it ends in `!`.
Verified against the live API: `Dev-Only-PasswOrd!` and `Dev-Only-Passw0rd` both fail with
"The email or password is incorrect".

---

## Librarian — web dashboard

Start here. This role owns the S1 screens that talk to the real API.

```
librarian@studyhive.dev
```

```
Dev-Only-Passw0rd!
```

Sees: Booking requests, Request detail, Students, Approvals, Rooms, Reports.

## Store Officer — web dashboard

```
storeofficer@studyhive.dev
```

```
Dev-Only-Passw0rd!
```

Sees: Consumables, Low stock, Reservations, Suppliers.

## Admin — web dashboard

```
admin@studyhive.dev
```

```
Dev-Only-Passw0rd!
```

Sees: everything, plus Users and Settings.

## Student — mobile app only

```
student@studyhive.dev
```

```
Dev-Only-Passw0rd!
```

Creates and tracks their own booking requests.

---

## The two URLs

| App                              | URL                                           | Sign in as                       |
| -------------------------------- | --------------------------------------------- | -------------------------------- |
| **Web** — staff dashboard | [http://localhost:5173](http://localhost:5173) | librarian / storeofficer / admin |
| **Mobile** — student app  | [http://localhost:8090](http://localhost:8090) | student                          |

Each app rejects the other's accounts **on purpose**. The student cannot sign in to the web
dashboard and staff cannot sign in to the mobile app; both say so clearly. That is the design, not a
bug.

The mobile app is the Flutter build running in a browser. Narrow the window, or press F12 and turn on
the device toolbar, or it stretches across the whole page.

---

## "The email or password is incorrect" when you are sure it is right

This is a real 401 — the request reached the API and the password did not match. In practice it is
almost always one of these:

1. **Your browser's password manager autofilled the field.** It silently replaces what you pasted
   with a saved credential, and the screen has no way to tell you. Clear the field completely, then
   paste. Or open a private/incognito window so autofill stays out of it.
2. **A stray space** got copied at the start or end.
3. **`PasswOrd` with a letter O** instead of a zero.

To find out for certain which one, open the browser console (F12) on the sign-in page and paste:

```js
await fetch("http://localhost:5299/api/auth/login", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ email: "librarian@studyhive.dev", password: "Dev-Only-Passw0rd!" }),
}).then(r => r.json().then(d => ({ status: r.status, user: d.user })));
```

If that returns `status: 200`, the account and the API are fine and the problem is what the form is
sending — so it is autofill or a typo. If it returns 401, tell me and I will look further.

**"Something went wrong. Please try again."** is a different failure. That one only appears when the
request never completes, which means the API is not running or is not reachable. Check
[http://localhost:5299/health](http://localhost:5299/health).

---

## Start everything

Four terminals from the repo root.

**1. Database**

```bash
docker compose up -d db
```

**2. API** — seeds the accounts on startup

```bash
dotnet run --project api/src/StudyHive.Api --no-launch-profile --urls http://localhost:5299
```

Confirm it is up:

```bash
curl http://localhost:5299/health
```

**3. Web app**

```bash
npm run dev --prefix web
```

**4. Mobile app** — build once, then serve it

```bash
flutter build web --dart-define=API_BASE_URL=http://localhost:5299
```

```bash
python -m http.server 8090 --directory mobile/build/web
```

The `--dart-define` matters. Without it the app defaults to `http://10.0.2.2:5299`, the Android
emulator's alias for the host, which a desktop browser cannot reach — sign-in then fails with a
network error that looks like the API is down when it is not.

`http://localhost:8090` is in `Cors:AllowedOrigins` alongside the web app's `5173`. Serve the mobile
build on a different port and the browser blocks every API call.

---

## What you will see

The seeder creates real data, not empty screens:

|                     |                                 |
| ------------------- | ------------------------------- |
| Booking requests    | 10, across 8 different statuses |
| Workflow executions | 8, with 28 step logs            |
| Consumables         | 3                               |
| Student profiles    | 2                               |

Statuses cover Draft, Processing, PendingApproval (×3), Approved, Rejected, Completed, Cancelled and
Failed — so the request timeline has something to show in every state, including the failure path.

**Which screens are real:** sign-in (W-01), Booking requests (W-10), Request detail (W-11) and
Students (W-12) talk to the live API — those are S1's and they are finished. The S2/S3/S4 screens
render the reference layout from a fixture set and label themselves as a development preview.
Anything whose endpoint does not exist yet shows an honest "not built yet" state rather than fake
numbers.

---

## Related

- [`DATABASE.md`](DATABASE.md) — the full schema, and how to point at Neon instead of Docker
- [`.env.example`](.env.example) — connection string template
- [`DOCS/S1_Scope_And_Handoff.md`](DOCS/S1_Scope_And_Handoff.md) — what S1 owns
