# Changelog

## [1.4.3] - 2026-11-26
### Changed
- **視訊渲染與低延遲追幀**：
  - `MirrorSessionService` 解碼迴圈加入積壓追幀策略（`reader.Count > 0` 時僅更新 DPB、略過過時幀的 GPU 渲染），快速消除 Wi-Fi 抖動或視窗縮放造成的延遲累積。
  - `D3D11SwapChainRenderer` 快取 `ID3D11VideoProcessorInputView`（依 DXVA DPB subresource index 複用），並於支援時啟用 `DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT` 與 `SetMaximumFrameLatency(1)` 降低顯示管線延遲。
  - `Nv12ColorConverter` 改採雙像素共用 UV 計算與大畫面 `Parallel.For` 並行轉換，顯著提升 CPU 回退路徑效能。
- **音訊與 Media Foundation Zero-Allocation**：
  - 新增 `PcmRingBuffer` 取代 `WasapiAudioPlayer` 內部 `Channel<byte[]>`，將 `IAudioPlayer.WritePcm` 升級為 `ReadOnlySpan<byte>` 並設定 80ms 延遲上限；同步消除 `WasapiAudioPlayer`／`WaveOutAudioPlayer` 迴圈內每次配置 `WaitHandle[]`。
  - `MediaFoundationH264Decoder`（CPU 回退輸出）與 `MediaFoundationAacDecoder` 複用原生 `IMFSample`／`IMFMediaBuffer` 並以 `ReadOnlySpan<byte>` 回傳 PCM；快取 `IsHevcDecoderAvailable()` 偵測結果。
- **網路與 ADB 加速**：
  - `AdbSmartSocketClient` 新增 `TryForwardPortAsync`／`TryRemoveForwardAsync`，`AdbService` 優先直連 ADB daemon 轉發連接埠（失敗再退回 CLI），減少連線／重連啟動子行程延遲。
  - `ScrcpyProtocolClient` 將視訊 Socket 接收緩衝區擴大至 512KB、合併連續控制封包寫入並縮短連線初期輪詢間隔（前 10 次 25ms）。

### Fixed
- `D3D11SwapChainRenderer` 改於 `ThreadPool` 非同步觸發 `DeviceLost` 事件，避免於 `_renderLock` 內同步回呼引發死鎖風險。
- `MirrorPage._rendererAttached` 改為 `volatile`，確保 UI 與背景解碼執行緒間的可見性一致。

### Documentation & Release
- 新增 MIT License、貢獻指南、安全性政策與第三方授權通知。
- 補齊使用指南、系統架構與技術文件、系統設計文件、開發指南、發佈流程、FAQ 與隱私權說明。
- 建立 `mark216tw/android-sync` 公開儲存庫，發佈 x64／ARM64 非 Self-Contained ZIP（不提供安裝程式）。

## [1.4.2] - 2026-11-26
### Added
- 新增桌面快捷鍵（`Alt+O` 切換息屏、`Alt+S` 截圖、`Alt+F` 貼合比例、`Alt+T` 視窗置頂、`Alt+M` 音訊靜音、`Alt+R` 旋轉畫面）。
- 底部提示列（`StatusToastBar`）新增自動消失計時（一般訊息 3 秒、錯誤訊息 5 秒），避免長期遮擋手機畫面底部。

### Changed
- 滑鼠拖曳事件新增同座標去重與 4ms（250Hz）節流，降低高回報率滑鼠對 Android 輸入佇列的負擔。
- 音訊封包讀取新增可重用緩衝區（`ReadNextAudioPacketViewAsync`），消除 AAC 串流讀取與靜音時的每包配置；`scrcpy-server` 推送加入裝置與檔案簽章快取，加快重連速度。
- `D3D11SwapChainRenderer` 移除每幀無條件全畫面 GPU 備份拷貝，改為截圖直讀 `_cachedBackBuffer`、縮放時按需備份，並快取 Video Processor 裁切／目標矩形減少重複 COM 呼叫。

### Fixed
- 修正截圖複製剪貼簿後 `InMemoryRandomAccessStream` 過早釋放導致貼上失敗問題（補上 `Clipboard.Flush()`）。
- 修正拖曳中途觸發 `PointerCanceled` 時未補送 `Up` 事件導致手機觸控卡住的問題。
- 修正高 DPI（125%／150%／200%）螢幕下「貼合手機比例」未依 `RasterizationScale` 換算邊框尺寸的問題。

## [1.4.1] - 2026-11-26
### Changed
- 縮小「畫面同步」右側快捷工具列按鈕尺寸（`44×44` → `32×32`、圖示 `14px`）與間距，並改用精簡 1px 分隔線，減少空間佔用並讓一般視窗高度即可完整顯示所有按鈕。

### Fixed
- 修正深色模式下（特別是 Windows 系統為淺色時）視窗右上角標題列按鈕不可見問題：同步設定 `DWMWA_USE_IMMERSIVE_DARK_MODE` 與完整的 `AppWindowTitleBar` 背景／按鈕色彩並跟隨主題背景色。

## [1.4.0] - 2026-11-26
### Added
- **H.265 (HEVC) 硬體解碼與即時效能 HUD**：支援於設定頁切換 H.264／H.265 編碼格式（無 HEVC 解碼器時自動退回 H.264），並於畫面左上角提供可切換的效能 HUD（FPS、平均解碼毫秒、位元率、編碼／硬解狀態、音訊模式）。
- **WASAPI 低延遲音訊與 AAC 壓縮串流**：新增零第三方依賴的 `WasapiAudioPlayer`（事件驅動、自動跟隨 Windows 預設音訊裝置切換，失敗時退回 `WaveOutAudioPlayer`）與 `MediaFoundationAacDecoder`（`audio_codec=aac`，節省約 90% Wi-Fi 音訊頻寬，保留 RAW PCM 切換）。
- **ADB Smart Socket 即時裝置追蹤**：新增 `AdbSmartSocketClient` 直連 `127.0.0.1:5037`（`host:devices-l`／`host:track-devices-l`），免輪詢即時更新裝置清單並快取工具路徑。
- **拖放傳檔與 APK 一鍵安裝**：拖曳檔案至同步畫面自動推送至 `/sdcard/Download/` 並通知媒體掃描，拖曳 `.apk` 自動執行 `adb install -r`。
- **一鍵 GPU 截圖、視窗置頂、精簡模式與比例貼合**：右側工具列新增從當前 GPU 紋理擷取 PNG 並複製剪貼簿、視窗最上層釘選、隱藏側欄精簡浮動視窗、自動貼合手機長寬比。
- **進階觸控與手勢**：新增 `Ctrl + 滑鼠拖曳` 模擬雙指縮放（Pinch-to-Zoom），以及觸控螢幕／手寫筆多點觸控透傳。
- **串流中斷自動重連**：意外斷線或 D3D11 Device Lost 時以指數退避自動重連（可於設定頁開關）。

### Fixed
- 修正 `MainWindow.OnWindowClosing` 非同步關閉競態，確保關閉前完整儲存視窗狀態並清理 `adb forward` 與 `scrcpy-server` 行程。
- 修正 `MediaFoundationH264Decoder` 單次拉幀與忽略 `MF_E_NOTACCEPTING` 問題，改為完整 drain 迴圈。
- 修正 `D3D11SwapChainRenderer` 缺乏 `DXGI_ERROR_DEVICE_REMOVED / RESET` 偵測與恢復機制，並快取 `ID3D11VideoProcessorOutputView`、監聽 `CompositionScaleChanged` 改善高 DPI 與每幀效能。
- 以 `ArrayPool<byte>` 與解耦網路讀取／解碼有界通道大幅降低視訊串流 GC 壓力。

## [1.3.0] - 2026-11-26
### Added
- 新增 GPU 零拷貝渲染管線：於 `D3D11SwapChainRenderer` 啟用 `VIDEO_SUPPORT`、`ID3D10Multithread` 與 `IMFDXGIDeviceManager`，透過 Media Foundation DXVA 硬體解碼輸出 `ID3D11Texture2D`（NV12），再以 `ID3D11VideoProcessor` 直接於 GPU 完成裁切、色域轉換並輸出至 `SwapChainPanel`，全程免除 CPU 回讀與上傳（不支援時自動降級為 CPU 路徑）。
- 新增顯示模式與五款主題即時切換：支援「跟隨系統／淺色模式／深色模式」與「經典藍／黃色護眼／森林綠／暮色紫／中性灰」五款獨立淺深雙色主題，並同步系統深淺色變更與視窗標題列按鈕色彩。
- 新增單一實例（Mutex）防護：透過 `SingleInstanceGuard` 與具名事件避免重複開啟多視窗，再次啟動時自動還原並將既有視窗帶至前景。

## [1.2.0] - 2026-11-26
### Added
- 新增手機音訊即時同步（Android 11+）：透過 `scrcpy-server` 獨立音訊 Socket 接收 48kHz 16-bit 立體聲 RAW PCM 串流，並以 Windows 原生 `winmm`（`WaveOutAudioPlayer`）低延遲播放；不支援音訊擷取的裝置會自動降級維持純畫面同步。
- 於「畫面同步」右側快捷列新增電腦播放靜音切換按鈕，並於「設定」頁新增音訊同步開關。

## [1.1.0] - 2026-11-26
### Added
- 新增電腦鍵盤輸入同步：支援控制／導覽／功能鍵與組合快捷鍵（`INJECT_KEYCODE` + `AndroidMetaState`）、ASCII 字元輸入（`INJECT_TEXT`），以及中文 IME／Emoji 文字透過剪貼簿自動貼上注入。
- 新增電腦與手機雙向剪貼簿同步：支援接收裝置端 `TYPE_CLIPBOARD`／`TYPE_ACK_CLIPBOARD` 控制訊息、監聽 Windows 剪貼簿變更推送至手機（含防迴圈協調器）、`Ctrl+V` 強制同步貼上，並於「設定」頁新增雙向剪貼簿同步開關。

## [1.0.5] - 2026-11-26
### Fixed
- 修正畫面右側與下方出現綠色邊框的問題：於 Media Foundation H.264 解碼輸出讀取 `MF_MT_MINIMUM_DISPLAY_APERTURE`／`MF_MT_GEOMETRIC_APERTURE` 可視區域，並於 NV12 轉 BGRA 時以編碼高度定位 UV 平面且裁切去除 16×16 巨集區塊補齊區。

## [1.0.4] - 2026-11-26
### Fixed
- 修正 `SwapChainPanel` 畫面只顯示左上角的問題：透過 `IDXGISwapChain2::SetMatrixTransform` 將原始解析度的 composition swap chain 縮放至 `SwapChainPanel` 顯示區域大小，並快取最後一幀以支援靜止畫面即時縮放視窗。

## [1.0.3] - 2026-11-26
### Fixed
- 修正 `scrcpy-server` v5.0.1 串流標頭與封包旗標解析（4 byte `codecId` + 12 byte `SessionMeta`，以及 bit 63/62/61 旗標），解決傳入無效寬高導致 `Media Foundation SetInputType 失敗 (0xC00D36B4)` 的問題，同時保留 v2/v3 自動相容。

## [1.0.2] - 2026-11-26
### Fixed
- 修正 `adb forward` 模式下 `scrcpy-server` 尚未就緒即收到 EOF 導致「scrcpy 串流已關閉。」的問題，將讀取 dummy byte 納入重試迴圈。
- 移除 `scrcpy-server` v5.0.1 不再支援的 `send_codec_meta=true` 啟動參數（預設即啟用串流中繼資料，相容 v2/v3/v5）。
- 補齊 `scrcpy-server` 標準輸出／錯誤非同步讀取與錯誤訊息回報，避免管線阻塞並便於排錯。

## [1.0.1] - 2026-11-26
### Changed
- 預設 `scrcpy-server` 版本升級至 `5.0.1`，並將 `TYPE_SET_DISPLAY_POWER` 控制封包對齊 v3.0+／v5.0.1 協議。
- 於「設定」頁新增 `scrcpy-server` 版本號設定欄位，支援自行切換版本。

## [1.0.0] - 2026-11-26
### Added
- 建立 .NET 10 + WinUI 3 桌面應用程式「Android同步」（`Com.AndroidSync.App`）。
- 新增 USB 有線裝置偵測、USB 轉 Wi-Fi 無線連線、Android 11+ 無線配對與連線功能。
- 新增 `scrcpy-server` 協議用戶端（視訊串流與控制封包序列化）。
- 新增 Windows Media Foundation H.264 解碼與 Direct3D 11 `SwapChainPanel` 渲染管線。
- 新增滑鼠座標映射（點擊、長按、拖曳、滾輪、右鍵返回、中鍵主畫面）與側邊快捷操作列。
- 新增應用程式圖示資產與視窗狀態記憶（含 `DisplayArea` 防越界還原）。
- 新增單元測試專案 `AndroidSync.Tests`。
