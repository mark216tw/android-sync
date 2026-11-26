# Android同步（AndroidSync）

[![Release](https://img.shields.io/github/v/release/mark216tw/android-sync?display_name=tag&sort=semver)](https://github.com/mark216tw/android-sync/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2011%20(22000%2B)-0078D4)](https://www.microsoft.com/windows/windows-11)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)

Windows 11 原生 Android 畫面即時同步與控制桌面應用程式，使用 **.NET 10 + WinUI 3** 打造。透過 USB 或 Wi-Fi 連接 Android 手機，以 Windows 滑鼠、鍵盤與觸控螢幕直接操作手機，並提供低延遲的 GPU 零拷貝視訊與 WASAPI 音訊播放。

> 本專案採用 ADB + `scrcpy-server` 協議，並以 Windows Media Foundation（DXVA）與 Direct3D 11 原生管線渲染，不依賴第三方執行期套件。

---

## 功能特色

### 連線管理
- **USB 有線連線**：自動偵測已連線裝置，顯示型號、序號、授權狀態與連線類型。
- **Wi-Fi 無線連線**：支援 `adb connect`、Android 11+ 無線偵錯配對（`adb pair`）、USB 一鍵轉無線（`adb tcpip`），並記憶常用無線裝置供一鍵重連。
- **ADB Smart Socket**：直連 `127.0.0.1:5037`，透過 `host:track-devices-l` 即時更新裝置清單，免輪詢。
- **串流中斷自動重連**：連線意外中斷或 D3D11 裝置遺失時，以指數退避自動重建工作階段。

### 畫面同步
- **低延遲硬體解碼**：H.264／H.265 (HEVC) 串流，優先使用 Media Foundation DXVA 硬體解碼。
- **GPU 零拷貝渲染**：NV12 紋理直接交由 `ID3D11VideoProcessor` 完成裁切、色域轉換並輸出至 WinUI 3 `SwapChainPanel`。
- **自動降級**：系統不支援硬解或 HEVC 時，自動退回 CPU NV12→BGRA 轉換路徑。
- **高 DPI 與視窗縮放**：使用 `IDXGISwapChain2::SetMatrixTransform` 維持長寬比，支援 125%／150%／200% 縮放。
- **效能 HUD**：即時顯示 FPS、平均解碼毫秒、位元率、編碼格式與硬解狀態。

### 操作與桌面整合
- **滑鼠**：左鍵點擊／長按／拖曳、滾輪捲動、右鍵返回、中鍵主畫面。
- **鍵盤**：完整按鍵與組合鍵映射；ASCII 走 `INJECT_TEXT`，中文 IME／Emoji 透過剪貼簿貼上。
- **多點觸控與手勢**：支援 Windows 觸控螢幕／手寫筆多點透傳，以及 `Ctrl + 拖曳` 模擬雙指縮放。
- **雙向剪貼簿**：電腦與手機剪貼簿同步，含防迴圈協調與 `Ctrl+V` 強制貼上。
- **拖放傳檔**：檔案拖入畫面即推送至手機 `Download` 資料夾；`.apk` 自動安裝。
- **桌面快捷鍵**：`Alt+O` 息屏、`Alt+S` 截圖、`Alt+F` 貼合比例、`Alt+T` 置頂、`Alt+M` 靜音、`Alt+R` 旋轉。
- **視窗工具**：一鍵 GPU 截圖（存 PNG + 複製剪貼簿）、視窗置頂、精簡浮動模式、自動貼合手機比例。
- **外觀**：跟隨系統／淺色／深色模式，以及五款淺深雙色主題即時切換。

### 音訊
- Android 11+ 音訊同步：AAC 壓縮音訊（節省約 90% 頻寬）或 RAW PCM。
- Windows 原生 WASAPI 事件驅動低延遲播放，自動跟隨預設音訊裝置；失敗時退回 `winmm` WaveOut。

---

## 系統需求

### 執行發行版（Release ZIP）
| 項目 | 需求 |
| --- | --- |
| 作業系統 | Windows 11 build 22000（含）以上 |
| 架構 | x64 或 ARM64（請下載對應版本） |
| .NET | [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（非 Self-Contained） |
| Windows App Runtime | [Windows App SDK 1.8 Runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) |
| Android 裝置 | Android 11 以上建議；需開啟「USB 偵錯」或「無線偵錯」 |
| ADB | `adb.exe`（Android Platform Tools）。程式會自動偵測 `Tools/adb.exe`、`ANDROID_HOME`、`%LOCALAPPDATA%\Android\Sdk\platform-tools` 或系統 `PATH` |
| scrcpy-server | 已內附於發行版 `Tools/scrcpy-server`；亦可於設定頁指定其他路徑與版本 |

### 開發環境
- Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（10.0.4xx）
- Visual Studio 2022/2026（選用）或 Rider
- 可執行 `dotnet build`／`dotnet test` 的終端機

---

## 快速開始

1. 至 [Releases](https://github.com/mark216tw/android-sync/releases) 下載符合系統架構的 ZIP：
   - `AndroidSync-<版本>-win-x64-non-self-contained.zip`
   - `AndroidSync-<版本>-win-arm64-non-self-contained.zip`
2. 解壓縮後安裝上表所列的 .NET 10 Runtime 與 Windows App Runtime 1.8。
3. 確認電腦已安裝 Android Platform Tools，且 `adb` 可於 `PATH` 或 `%LOCALAPPDATA%\Android\Sdk\platform-tools` 找到。
4. 於手機開發者選項開啟「USB 偵錯」，以 USB 連接電腦並在手機上允許偵錯授權。
5. 執行 `AndroidSync.exe`，於「裝置」頁選擇手機後按「開始同步畫面」。

完整操作說明請見 [docs/USAGE.md](docs/USAGE.md)。

---

## 常見問題

- **找不到 adb 或 scrcpy-server**：確認 `adb.exe` 與 `Tools/scrcpy-server` 存在，或於「設定」頁指定路徑後儲存。
- **裝置顯示未授權**：解鎖手機並在「允許 USB 偵錯」對話框按允許；必要時重新插拔 USB。
- **無法使用無線連線**：確認手機與電腦位於同一區域網路，且防火牆允許 ADB；Android 11+ 需先完成無線偵錯配對。
- **沒有畫面但有聲音**：可能是硬體解碼器不相容，請於設定頁切換 H.264，或更新顯示卡驅動程式。

更多排解方式請見 [docs/FAQ.md](docs/FAQ.md)。

---

## 建置與測試

```powershell
# 還原與建置（Debug）
dotnet build AndroidSync.sln

# 執行單元測試
dotnet test AndroidSync.sln -p:Platform=x64
```

發行非 Self-Contained 壓縮檔（x64／ARM64）：

```powershell
dotnet publish src/AndroidSync/AndroidSync.csproj -c Release -r win-x64 --self-contained false `
  -p:PlatformTarget=x64 -p:WindowsAppSDKSelfContained=false `
  -p:WindowsPackageType=None -o publish/x64

dotnet publish src/AndroidSync/AndroidSync.csproj -c Release -r win-arm64 --self-contained false `
  -p:Platform=arm64 -p:PlatformTarget=arm64 -p:WindowsAppSDKSelfContained=false `
  -p:WindowsPackageType=None -o publish/arm64
```

詳細開發與發佈流程請見 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) 與 [docs/RELEASE.md](docs/RELEASE.md)。

---

## 專案結構

```
src/AndroidSync/            WinUI 3 應用程式
  Views/                    MainWindow、DeviceListPage、MirrorPage、SettingsPage
  ViewModels/               MVVM ViewModel（CommunityToolkit.Mvvm）
  Models/                   設定、裝置、鏡像選項等資料模型
  Services/                 ADB、裝置管理、鏡像工作階段、設定、主題、視窗狀態
  Protocol/                 scrcpy-server 協議與控制封包序列化
  Rendering/                Media Foundation 解碼與 Direct3D 11 渲染
  Audio/                    WASAPI／WaveOut 播放與 AAC 解碼
  Helpers/                  座標映射、鍵盤映射、剪貼簿協調、截圖等
  Styles/                   Fluent 樣式與五款主題資源字典
  Strings/zh-Hant/          繁體中文資源
  Assets/                   應用程式圖示
  Tools/scrcpy-server       內附 scrcpy-server
tests/AndroidSync.Tests/    xUnit 單元測試
docs/                       使用、架構、設計、開發、發佈與隱私文件
```

---

## 文件導覽

| 文件 | 說明 |
| --- | --- |
| [docs/USAGE.md](docs/USAGE.md) | 安裝、連線、操作與快捷鍵完整指南 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 系統架構、執行緒模型與各層技術說明 |
| [docs/DESIGN.md](docs/DESIGN.md) | 系統設計文件：需求、元件、流程、狀態機與風險 |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | 開發環境、建置、測試與除錯 |
| [docs/RELEASE.md](docs/RELEASE.md) | 版本規則、封裝與 GitHub Release 流程 |
| [docs/FAQ.md](docs/FAQ.md) | 常見問題與疑難排解 |
| [docs/PRIVACY.md](docs/PRIVACY.md) | 隱私與資料處理說明 |
| [docs/SPEC.md](docs/SPEC.md) | 產品功能規格 |
| [docs/DEV_LOG.md](docs/DEV_LOG.md) | 開發紀錄 |
| [CHANGELOG.md](CHANGELOG.md) | 版本異動紀錄 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 貢獻指南 |
| [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) | 第三方元件授權 |

---

## 授權

本專案以 [MIT License](LICENSE) 釋出，Copyright © 2026 mark216tw。

內附之 `scrcpy-server` 屬於 [scrcpy](https://github.com/Genymobile/scrcpy) 專案，依 Apache License 2.0 授權；`adb` 未隨本專案散布。詳見 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 致謝

- [Genymobile/scrcpy](https://github.com/Genymobile/scrcpy)：`scrcpy-server` 協議與實作
- [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
