## Local Release Simulation for Eling Desktop + Backend

Same flow as `.github/workflows/release.yml` but run locally:

```powershell
# 1. Publish Desktop (self-contained)
dotnet publish src/desktop/Eling.Desktop -c Release -r win-x64 --self-contained true -o publish

# 2. Publish Backend (self-contained)
dotnet publish src/backend/Eling.Backend -c Release -r win-x64 --self-contained true -p:ElingSkipDashboard=true -o publish-backend

# 3. Merge backend into shared folder
Copy-Item publish-backend\eling-backend.* publish\ -Force
Copy-Item publish-backend\*.dll publish\ -Force
Remove-Item publish-backend -Recurse -Force

# 4. Clean PDBs
Get-ChildItem publish -Recurse -Filter "*.pdb" | Remove-Item -Force

# 5. Package
Compress-Archive -Path publish\* -DestinationPath eling-win-x64.zip

# Size: ~63 MB compressed, ~143 MB uncompressed
# Both exes share runtime + DLLs, ~60% savings vs 2x single-file
```

Notes:
- Trimming cannot be used (trimmed Desktop DLLs are incompatible with Backend)
- Both self-contained, shared folder
- Use `ElingSkipDashboard=true` to skip pnpm dashboard build
- Replace `win-x64` with other RID for cross-platform testing
