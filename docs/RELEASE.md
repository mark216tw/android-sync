# 版本與發佈流程

本文件說明版本規則、建置產物、ZIP 命名、GitHub Release 步驟與發佈前檢查。開發環境請見 [DEVELOPMENT.md](DEVELOPMENT.md)。

---

## 1. 版本規則

版本定義於 `Directory.Build.props`：

```xml
<Version>1.4.3</Version>
<AssemblyVersion>1.4.0.0</AssemblyVersion>
<FileVersion>1.4.3.0</FileVersion>
```

- `Version`：語意化版本 `主.次.修`，每次功能或修正變更時遞增。
- `AssemblyVersion`：僅在主／次版變更時調整。
- `FileVersion`：與 `Version` 同步。
- Git tag 使用 `v<Version>`，例如 `v1.4.3`。

同時更新：

- `CHANGELOG.md`：技術面向的版本異動。
- `RELEASE_NOTES.md`：使用者面向的發行說明。
- `docs/DEV_LOG.md`：本次開發紀錄。

---

## 2. 發佈產物

本專案**只提供 ZIP 壓縮檔**，不提供安裝程式（MSI／MSIX）。

| 平台 | 檔名 |
| --- | --- |
| x64 | `AndroidSync-<版本>-win-x64-non-self-contained.zip` |
| ARM64 | `AndroidSync-<版本>-win-arm64-non-self-contained.zip` |

- 使用 **framework-dependent（非 Self-Contained）** 模式。
- 需安裝 .NET 10 Runtime 與 Windows App Runtime 1.8。
- 內含 `Tools/scrcpy-server`、`Assets/`、`LICENSE` 與 `THIRD_PARTY_NOTICES.md`。

---

## 3. 發佈前檢查

1. 確認 `Directory.Build.props` 版本正確。
2. 更新 `CHANGELOG.md`、`RELEASE_NOTES.md`、`docs/DEV_LOG.md`。
3. 執行建置與測試：

```powershell
dotnet build AndroidSync.sln --no-restore -m:1
dotnet test AndroidSync.sln -p:Platform=x64
```

4. 確認 0 警告、0 錯誤，測試全數通過。
5. 以實體裝置進行煙霧測試（至少 USB 連線、畫面、輸入、音訊、關閉清理）。

---

## 4. 建置 ZIP

### 4.1 清理舊輸出

```powershell
Remove-Item -Recurse -Force publish, artifacts -ErrorAction SilentlyContinue
```

### 4.2 發佈 x64 與 ARM64

```powershell
dotnet publish src/AndroidSync/AndroidSync.csproj -c Release -r win-x64 `
  --self-contained false `
  -p:PlatformTarget=x64 `
  -p:WindowsAppSDKSelfContained=false `
  -p:WindowsPackageType=None `
  -o publish/x64

dotnet publish src/AndroidSync/AndroidSync.csproj -c Release -r win-arm64 `
  --self-contained false `
  -p:Platform=arm64 `
  -p:PlatformTarget=arm64 `
  -p:WindowsAppSDKSelfContained=false `
  -p:WindowsPackageType=None `
  -o publish/arm64
```

### 4.3 加入授權文件

```powershell
Copy-Item LICENSE publish/x64/
Copy-Item LICENSE publish/arm64/
Copy-Item THIRD_PARTY_NOTICES.md publish/x64/
Copy-Item THIRD_PARTY_NOTICES.md publish/arm64/
```

### 4.4 產生 ZIP

為避免解壓縮後檔案散落，ZIP 內含一層版本資料夾：

```powershell
$version = '1.4.3'
New-Item -ItemType Directory -Force artifacts | Out-Null

$stageX64 = "artifacts/AndroidSync-$version-win-x64"
$stageArm = "artifacts/AndroidSync-$version-win-arm64"
Copy-Item publish/x64 $stageX64 -Recurse
Copy-Item publish/arm64 $stageArm -Recurse

Compress-Archive -Path $stageX64 -DestinationPath "artifacts/AndroidSync-$version-win-x64-non-self-contained.zip" -Force
Compress-Archive -Path $stageArm -DestinationPath "artifacts/AndroidSync-$version-win-arm64-non-self-contained.zip" -Force
```

### 4.5 驗證

- 檢查 ZIP 大小合理（通常約 15–25 MB，視版本而定）。
- 解壓縮後確認 `AndroidSync.exe`、`Tools/scrcpy-server`、`Assets/AppIcon.ico` 存在。
- 以 PowerShell 檢查 PE 架構（見 [DEVELOPMENT.md](DEVELOPMENT.md)）。
- 在乾淨環境確認 .NET 10 Runtime 與 Windows App Runtime 已安裝後可啟動。

---

## 5. 建立 GitHub Release

使用 GitHub CLI（需先 `gh auth login`）：

```powershell
$version = '1.4.3'

gh release create "v$version" `
  "artifacts/AndroidSync-$version-win-x64-non-self-contained.zip" `
  "artifacts/AndroidSync-$version-win-arm64-non-self-contained.zip" `
  --repo mark216tw/android-sync `
  --title "Android同步 v$version" `
  --notes-file RELEASE_NOTES.md
```

若 Release 已存在，可改用：

```powershell
gh release upload "v$version" `
  "artifacts/AndroidSync-$version-win-x64-non-self-contained.zip" `
  "artifacts/AndroidSync-$version-win-arm64-non-self-contained.zip" `
  --repo mark216tw/android-sync --clobber
```

---

## 6. 授權

- 專案本體：MIT License（`LICENSE`）。
- `scrcpy-server`：Apache License 2.0，隨程式散布並保留聲明（`THIRD_PARTY_NOTICES.md`）。
- `adb`：未隨程式散布，使用者需自行安裝。
- GitHub 儲存庫的授權顯示以根目錄 `LICENSE` 為準，發佈後應顯示 **MIT license**。

---

## 7. 發佈後檢查

1. Release 頁面顯示兩個 ZIP 與發行說明。
2. 下載連結可用，檔案可正常解壓縮。
3. 儲存庫首頁顯示 MIT License。
4. README 的下載連結與版本資訊正確。
5. 必要時更新儲存庫 About 描述與 Topics。

---

## 8. 回復與 Hotfix

- 若發佈版本有重大問題，建立新版本修正，不覆寫既有 tag。
- 必要時將 Release 標記為 Pre-release 或移除有問題的附件。
- Hotfix 仍須更新版本、CHANGELOG、RELEASE_NOTES 與 DEV_LOG。
