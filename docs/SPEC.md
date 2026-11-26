# 產品功能規格

本文件描述「Android同步」的功能範圍與產品層級需求。實作架構請見 [ARCHITECTURE.md](ARCHITECTURE.md)，設計取捨請見 [DESIGN.md](DESIGN.md)。

---

## 1. 產品概述

「Android同步」是一款 Windows 11 原生桌面應用程式（.NET 10 + WinUI 3），透過 USB 或 Wi-Fi 連接 Android 手機，即時呈現手機畫面，並讓使用者以 Windows 滑鼠、鍵盤、觸控螢幕與側邊快捷列操作手機。

### 目標使用者

- 需要在電腦前專注工作，同時查看與回覆手機訊息的使用者。
- 需要以鍵盤滑鼠操作手機的開發者與測試人員。
- 需要將手機畫面投放至 Windows 進行展示的使用者。

---

## 2. 功能範圍

### 2.1 第一階段（MVP）

1. **裝置連線管理**
   - USB 有線連線：掃描並顯示型號、序號、授權狀態。
   - Wi-Fi 無線連線：
     - USB 一鍵切換無線（`adb tcpip` + 查詢 IP + `adb connect`）。
     - Android 11+ 無線偵錯配對（`adb pair`）與連線。
     - 記憶常用無線裝置並支援重連。
2. **即時畫面同步**
   - 推送並啟動 `scrcpy-server`，接收 H.264 串流。
   - Media Foundation 解碼與 Direct3D 11 渲染至 `SwapChainPanel`。
   - 維持長寬比與處理直向／橫向變化。
   - 可調整最大解析度、位元率與最大 FPS。
3. **滑鼠控制與側邊快捷列**
   - 左鍵點擊／長按／拖曳、滾輪、右鍵返回、中鍵主畫面。
   - 快捷列：返回、主畫面、多工、音量＋／－、電源、息屏、旋轉、斷線。
4. **系統必備項**
   - 完整 App 圖示（執行檔、視窗、工作列）。
   - 視窗大小、位置與最大化狀態記憶，還原前檢查工作區。
   - 繁體中文預設資源。

### 2.2 第二階段與後續擴充

1. **鍵盤輸入**：完整鍵碼與組合鍵；ASCII 直接注入；中文 IME／Emoji 以剪貼簿貼上。
2. **雙向剪貼簿**：防迴圈協調、`Ctrl+V` 強制同步。
3. **音訊同步**：Android 11+ RAW PCM／AAC，WASAPI 低延遲播放與 WaveOut 回退。
4. **進階視訊**：H.265 (HEVC)、DXVA GPU 零拷貝、CPU 回退、D3D11 裝置遺失恢復、效能 HUD。
5. **桌面工具**：一鍵截圖、視窗置頂、精簡模式、貼合比例、拖放傳檔與 APK 安裝。
6. **進階輸入**：`Ctrl + 拖曳` 雙指縮放、觸控螢幕／手寫筆多點透傳、桌面快捷鍵。
7. **穩定性**：ADB Smart Socket 即時裝置追蹤、串流中斷自動重連。
8. **外觀與體驗**：顯示模式與五款主題、單一實例、標題列色彩同步。

---

## 3. 系統架構摘要

- **Presentation**：WinUI 3（`MainWindow`、`DeviceListPage`、`MirrorPage`、`SettingsPage`）+ CommunityToolkit.Mvvm。
- **Services**：`AdbService`、`DeviceManagerService`、`MirrorSessionService`、`SettingsService`、`ThemeService`、`WindowStateService`、`SingleInstanceGuard`。
- **Protocol**：`ScrcpyProtocolClient`（視訊／音訊／控制通道、握手、封包分幀）與 `ScrcpyControlSerializer`。
- **Rendering**：`MediaFoundationH264Decoder`、`D3D11SwapChainRenderer`、`Nv12ColorConverter`。
- **Audio**：`WasapiAudioPlayer`、`WaveOutAudioPlayer`、`MediaFoundationAacDecoder`、`PcmRingBuffer`。
- **Helpers**：座標／鍵盤映射、剪貼簿協調、手勢計算、截圖。

詳細資料流與執行緒模型請見 [ARCHITECTURE.md](ARCHITECTURE.md)。

---

## 4. 非功能需求

| 類別 | 需求 |
| --- | --- |
| 平台 | Windows 11 build 22000 以上；x64 與 ARM64 |
| 效能 | 1080p／60 FPS 為目標；GPU 零拷貝路徑避免 CPU 回讀 |
| 延遲 | 低延遲解碼、最大呈現延遲 1、有界佇列與追幀 |
| 穩定性 | 網路中斷與 D3D11 裝置遺失可自動恢復 |
| 相容性 | `scrcpy-server` v2/v3/v5 握手；HEVC 不可用時退回 H.264 |
| 隱私 | 不收集遙測；設定僅存本機 |
| 可存取性 | 控制項具 Automation Name；圖示按鈕具工具提示 |
| 可維護性 | 分層、DI、單元測試、0 警告建置 |

---

## 5. 使用者介面

| 頁面 | 內容 |
| --- | --- |
| 裝置 | 已連線裝置列表、重新掃描、無線連線、無線配對、常用無線裝置 |
| 畫面同步 | `SwapChainPanel`、狀態提示、錯誤列、效能 HUD、右側工具列 |
| 設定 | 外觀、工具路徑、串流參數、剪貼簿、音訊、自動重連、HUD |

---

## 6. 限制與非目標

- 不提供螢幕錄影。
- 不內建 ADB；需使用者自行安裝。
- 不支援 Windows 10 與 ARM32。
- 不提供跨網際網路或雲端連線。
- 部分 Android 客製系統可能限制音訊擷取或無線偵錯。

---

## 7. 版本

目前版本與異動請見 [CHANGELOG.md](../CHANGELOG.md) 與 [Releases](https://github.com/mark216tw/android-sync/releases)。
