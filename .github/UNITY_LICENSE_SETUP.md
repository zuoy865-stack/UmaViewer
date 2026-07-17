# Unity license setup for GitHub Actions

The Windows IL2CPP workflow needs a Unity license and Unity account credentials.
Never commit these values to the repository.

## Unity Personal

1. In Unity Hub, sign in and open **Preferences > Licenses**.
2. Click **Add** and activate a free Unity Personal license. Do this even when
   Hub already displays a license, so that the license file is created.
3. Locate `C:\ProgramData\Unity\Unity_lic.ulf` on Windows.
4. Open the repository's **Settings > Secrets and variables > Actions** page.
5. Add these repository secrets:
   - `UNITY_LICENSE`: the complete text of `Unity_lic.ulf`.
   - `UNITY_EMAIL`: the email address used for the Unity account.
   - `UNITY_PASSWORD`: the Unity account password.

## Unity Pro

Use `UNITY_EMAIL`, `UNITY_PASSWORD`, and `UNITY_SERIAL` instead.

After saving the secrets, re-run the failed workflow from the repository's
**Actions** page, or push another commit to `master`.
