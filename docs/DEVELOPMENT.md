# 開發指南

本文件說明本專案的開發環境、建置、測試、除錯與發佈方式。使用與操作說明請見 [USAGE.md](USAGE.md)。

---

## 1. 開發環境

| 項目 | 建議版本 |
| --- | --- |
| 作業系統 | Windows 11 build 22000 以上 |
| .NET SDK | .NET 10 SDK（10.0.4xx） |
| IDE | Visual Studio 2022/2026（含 Windows App SDK 工作負載）或 JetBrains Rider |
| 測試 | xUnit（由 `Directory.Packages.props` 管理版本） |

確認環境：

```powershell
dotnet --info
dotnet --list-sdks
```

> 本專案使用 Central Package Management；套件版本集中於 `Directory.Packages.props`，請勿在 `*.csproj` 寫死版本。

---

## 2. 取得原始碼與建置

```powershell
git clone https://github.com/mark216tw/android-sync.git
cd android-sync

# 還原
dotnet restore AndroidSync.sln

# 建置（Debug）
dotnet build AndroidSync.sln

# 若環境有多節點 XAML 編譯競態，可改用單節點
dotnet build AndroidSync.sln --no-restore -m:1
```

成功時應顯示 `0 警告`、`0 錯誤`。本專案啟用 `TreatWarningsAsErrors`，任何警告都會導致建置失敗。

---

## 3. 測試

```powershell
dotnet test AndroidSync.sln -p:Platform=x64
```

- 測試專案：`tests/AndroidSync.Tests`
- 框架：xUnit
- 測試涵蓋 ADB 解析、協議、座標／鍵盤映射、NV12 轉色、音訊緩衝、主題、視窗比例等。

新增測試時：

- 檔名與被測型別對應，例如 `CoordinateMapperTests.cs`。
- 優先測試純邏輯與可注入依賴的元件。
- 不依賴實體 Android 裝置或 GPU；硬體相關邏輯以 HResult、尺寸計算或狀態判斷測試。
- 不留 `Thread.Sleep` 造成的長時間等待。

---

## 4. 專案結構

```
src/AndroidSync/
  App.xaml(.cs)           DI Host、啟動流程
  Program.cs              自訂 Main、單一實例
  Views/                  XAML 頁面與視窗
  ViewModels/             MVVM
  Models/                 設定與資料模型
  Services/               ADB、裝置、鏡像、設定、主題、視窗
  Protocol/               scrcpy 協議與控制封包
  Rendering/              Media Foundation、D3D11、NV12 轉色
  Audio/                  AAC 解碼、WASAPI／WaveOut
  Helpers/                映射、剪貼簿、手勢、截圖
  Styles/                 AppStyles 與 Themes
  Strings/zh-Hant/        繁體中文資源
  Assets/                 圖示
  Tools/scrcpy-server     內附二進位
tests/AndroidSync.Tests/  單元測試
docs/                     文件
```

---

## 5. 程式碼規範

- C# 最新語法、file-scoped namespace、nullable reference types。
- 4 空格縮排；公開型別與檔名同名，一個檔案一個公開型別。
- 私有欄位 `_camelCase`；常數 PascalCase。
- 非同步方法以 `Async` 結尾；函式庫層加 `ConfigureAwait(false)`，UI 層不加。
- 不在 UI 執行緒做 I/O；長工作使用 `Task.Run` 或非同步流程。
- 使用者可見文字放 `Strings/zh-Hant/Resources.resw`，透過 `x:Uid` 或 `ResourceLoader` 取得。
- 顏色與筆刷放 `Styles/Themes`，不要在 XAML 寫死色碼。
- 不新增未在 `Directory.Packages.props` 宣告的套件。
- 不使用 `Console.WriteLine`／`Debug.WriteLine` 作為正式輸出；使用 `ILogger`。
- 不留 `TODO` 或測試假資料。

---

## 6. 除錯

### 6.1 日誌

- 使用 `Microsoft.Extensions.Logging`。
- `Debug` 組態輸出至 Visual Studio 的「輸出」視窗；正式版不輸出至主控台。
- `scrcpy-server` 的 stdout／stderr 會以 `[scrcpy-server]` 前綴記錄，連線失敗時會附帶最近日誌摘要。

### 6.2 常見建置問題

| 問題 | 原因 | 解法 |
| --- | --- | --- |
| `NETSDK1135`（`SupportedOSPlatformVersion` 高於 `TargetPlatformVersion`） | TFM 與最低平台不一致 | 使用 `net10.0-windows10.0.22000.0` 與 `TargetPlatformMinVersion=10.0.22000.0` |
| `dotnet test` 找不到最新組件 | 解決方案預設平台為 x64，輸出至 `bin\x64` | 加上 `-p:Platform=x64` |
| XAML 編譯互相取消 | 多節點並行建置競態 | 使用 `dotnet build --no-restore -m:1` |
| 讀取 `%UserProfile%\NuGet.Config` 遭拒 | 沙盒／權限限制 | 使用 `--no-restore`，或設定可讀取的 NuGet 設定 |
| 找不到 Windows App Runtime | 未安裝對應執行期 | 安裝 Windows App Runtime 1.8 |

### 6.3 執行注意事項

- 未封裝模式下，若使用 `WindowsAppSDKSelfContained=false`，需安裝 Windows App Runtime。
- 執行前請確認 `adb.exe` 可用，且手機已授權 USB 偵錯。
- 修改協議解析時，建議同時以 v2/v3 與 v5.0.1 的 `scrcpy-server` 驗證。

### 6.4 除錯 GPU 管線

- 若畫面黑屏，先確認顯示卡支援 D3D11 Video Processor。
- 可於日誌檢查是否出現「DXVA」或「CPU」路徑訊息。
- 使用效能 HUD 觀察 FPS、解碼時間與位元率。
- DeviceLost 會觸發自動重連；可暫時關閉自動重連以觀察錯誤。

---

## 7. 發佈（非 Self-Contained）

本專案發行版為 framework-dependent，提供 x64 與 ARM64。範例：

```powershell
# x64
dotnet publish src/AndroidSync/AndroidSync.csproj -c Release -r win-x64 `
  --self-contained false `
  -p:PlatformTarget=x64 `
  -p:WindowsAppSDKSelfContained=false `
  -p:WindowsPackageType=None `
  -o publish/x64

# ARM64（跨架構編譯）
dotnet publish src/AndroidSync/AndroidSync.csproj -c Release -r win-arm64 `
  --self-contained false `
  -p:Platform=arm64 `
  -p:PlatformTarget=arm64 `
  -p:WindowsAppSDKSelfContained=false `
  -p:WindowsPackageType=None `
  -o publish/arm64
```

輸出重點：

- `AndroidSync.exe`／`AndroidSync.dll`
- `Assets/`、`Tools/scrcpy-server`
- `Microsoft.WindowsAppRuntime.Bootstrap.dll`（framework-dependent 模式的啟動器）
- `AndroidSync.runtimeconfig.json` 僅引用 `Microsoft.NETCore.App`，代表非 Self-Contained

驗證執行檔架構（PowerShell）：

```powershell
function Get-PeMachine($path) {
  $b = [IO.File]::ReadAllBytes($path)
  $pe = [BitConverter]::ToInt32($b, 0x3C)
  switch ([BitConverter]::ToUInt16($b, $pe + 4)) {
    0x8664 { 'x64' }
    0xAA64 { 'ARM64' }
    default { 'other' }
  }
}
Get-PeMachine publish/x64/AndroidSync.exe
Get-PeMachine publish/arm64/AndroidSync.exe
```

封裝 ZIP 與發行流程請見 [RELEASE.md](RELEASE.md)。

---

## 8. 開發流程建議

1. 建立功能分支。
2. 變更程式碼與資源。
3. 更新 `docs/DEV_LOG.md`（記錄目標、變更、問題與待辦）。
4. 需要時更新 `README.md`、`CHANGELOG.md`、`RELEASE_NOTES.md`。
5. 執行 `dotnet build` 與 `dotnet test`。
6. 確認 0 警告、0 錯誤後提交 Pull Request。

> 版本號定義於 `Directory.Build.props`；功能變更時遞增 `Version`，主次版變更時才調整 `AssemblyVersion`。

---

## 9. 相關文件

- [ARCHITECTURE.md](ARCHITECTURE.md)
- [DESIGN.md](DESIGN.md)
- [RELEASE.md](RELEASE.md)
- [FAQ.md](FAQ.md)
- [CONTRIBUTING.md](../CONTRIBUTING.md)
