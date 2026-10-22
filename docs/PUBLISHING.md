# Publishing this repository

1. Create an empty GitHub repository, for example `inzone-mic-switch`. This folder already provides README, MIT LICENSE, and .gitignore.
2. Upload **the contents of this folder** as the repository root. Include `.github/workflows/build.yml`, `.gitignore`, and `.gitattributes`. Do not upload a parent research directory, Sony application files, personal captures, or generated `artifacts/` directories.
3. With Git installed, an alternative to browser upload is to run the following from this folder (replace the remote URL with your own):

```powershell
git init -b main
git add .
git status
git commit -m "Initial public release"
git remote add origin https://github.com/YOUR-ACCOUNT/inzone-mic-switch.git
git push -u origin main
```

4. Check the **Build and test** workflow in GitHub's Actions tab. Download its artifact, which contains the end-user ZIP and SHA-256 file, or build them locally with `Build.ps1`.
5. Create a GitHub Release with a version tag such as `v0.1.0`. Attach `InzoneMicSwitch-win-x64.zip` and its `.sha256` file. Describe the tested hardware and known limits from README. The automatically generated source archives do not contain runnable applications.

No automated release publishing, signing credentials, or tokens are configured. The workflow needs only read access to repository contents. The executable is unsigned; do not represent it as a Sony release.
