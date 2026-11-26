# 開發紀錄

最新紀錄放最上面。每次開發工作都要更新，不要事後補寫。

---

## 2026-11-26 - v1.4.3（文件、授權與發佈）

### 目標
補齊專案文件與 MIT 授權，建立公開 GitHub 儲存庫，並發佈 x64／ARM64 非 Self-Contained ZIP（不提供安裝程式）。

### 變更
- 根目錄：改寫 `README.md`；新增 `LICENSE`（MIT）、`CONTRIBUTING.md`、`SECURITY.md`、`THIRD_PARTY_NOTICES.md`、`.gitattributes`；更新 `RELEASE_NOTES.md` 至 `1.4.3`。
- `docs/`：新增 `USAGE.md`、`ARCHITECTURE.md`、`DESIGN.md`、`DEVELOPMENT.md`、`RELEASE.md`、`FAQ.md`、`PRIVACY.md`；更新 `SPEC.md` 為現行功能範圍。
- 發佈：以 `--self-contained false` 搭配 `WindowsAppSDKSelfContained=false` 產生 x64 與 ARM64 輸出，確認 `AndroidSync.exe` PE 架構分別為 x64 與 ARM64，封裝為 `AndroidSync-1.4.3-win-{x64,arm64}-non-self-contained.zip`。
- GitHub：建立公開儲存庫 `mark216tw/android-sync`，推送 `main`，建立 `v1.4.3` Release 並上傳兩個 ZIP；以 `LICENSE` 使儲存庫顯示 MIT License。

### 問題與解法
- ARM64 跨架構發佈需同時覆寫 `Platform`、`PlatformTarget` 與 RuntimeIdentifier；僅指定 `-r win-arm64` 會沿用專案預設的 x64 平台設定。
- 非 Self-Contained 版本需額外安裝 Windows App Runtime 1.8，已在 README 與發行說明標明。
- 本機已安裝 Windows App Runtime 1.8，x64 發行檔可正常啟動；ARM64 僅完成建置與架構驗證，未於 ARM64 實機執行。

### 待辦／已知限制
- 待於 ARM64 Windows 實機驗證啟動與影音管線。
- 待於實體 Android 裝置回歸驗證 H.265、AAC/WASAPI 與自動重連。

---

## 2026-11-26 - v1.4.3

### 目標
針對底層視訊渲染、Media Foundation 解碼、WASAPI 音訊、Socket 與 ADB 轉發路徑進行全套低改動、高收益效能與併發穩定性優化。

### 變更
- `src/AndroidSync/Rendering/D3D11SwapChainRenderer.cs`：
  - 新增 `ID3D11VideoProcessorInputView` 快取（依 DXVA DPB subresource index 複用，於 texture 切換／Resize／釋放時清理）。
  - 啟用 `DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT` 與 `IDXGISwapChain2::SetMaximumFrameLatency(1)`（建立失敗時自動退回 `Flags = 0` 並由 `_swapChainFlags` 保持 `ResizeBuffers` 一致）。
  - `DeviceLost` 事件改經由 `ThreadPool.QueueUserWorkItem` 移出 `_renderLock` 觸發，避免重入死鎖。
- `src/AndroidSync/Rendering/Nv12ColorConverter.cs`：改用 unsafe 指標、每 2 像素共用一次 UV 計算與大畫面 `Parallel.For` 並行轉色。
- `src/AndroidSync/Rendering/MediaFoundationH264Decoder.cs`、`Audio/MediaFoundationAacDecoder.cs`：
  - 快取 `IsHevcDecoderAvailable()` 結果（`Lazy<bool>`）。
  - 複用 CPU 視訊輸出與 AAC 解碼輸出的 `IMFSample`／`IMFMediaBuffer`，AAC 解碼改以 `PcmDecodedHandler(ReadOnlySpan<byte>)` 零配置回傳 PCM。
- `src/AndroidSync/Audio/PcmRingBuffer.cs`、`IAudioPlayer.cs`、`WasapiAudioPlayer.cs`、`WaveOutAudioPlayer.cs`：
  - 新增固定容量 `PcmRingBuffer`（支援 frame alignment 與 80ms 延遲上限裁切），`IAudioPlayer.WritePcm` 改接受 `ReadOnlySpan<byte>`。
  - 消除 `WasapiAudioPlayer`／`WaveOutAudioPlayer` 等待迴圈內重複配置 `WaitHandle[]`。
- `src/AndroidSync/Protocol/ScrcpyProtocolClient.cs`、`Services/MirrorSessionService.cs`、`Services/AdbSmartSocketClient.cs`、`Services/AdbService.cs`、`Views/MirrorPage.xaml.cs`：
  - `MirrorSessionService` 視訊解碼迴圈加入 `reader.Count == 0` 追幀渲染策略，音訊 RAW／AAC 皆改走零配置 `Span` 路徑。
  - `ScrcpyProtocolClient` 擴大視訊 `ReceiveBufferSize` 至 512KB、合併 `ControlWriteLoopAsync` 連續封包寫入、連線初期輪詢縮短至 25ms。
  - `AdbSmartSocketClient` 新增 `TryForwardPortAsync`／`TryRemoveForwardAsync` 供 `AdbService` 優先走 TCP 5037 轉發。
  - `MirrorPage._rendererAttached` 改為 `volatile`。
- `tests/AndroidSync.Tests/V14OptimizationTests.cs`、`Directory.Build.props`、`CHANGELOG.md`：新增 `PcmRingBuffer`、ADB Smart Socket 轉發與 `Nv12ColorConverter` 大畫面並行測試，版本遞增至 `1.4.3`。

### 問題與解法
- `dotnet build --no-restore -m:1` 預設以 solution 的 `x64` 平台輸出至 `bin\x64\Debug`，單獨執行 `dotnet test` 需補上 `-p:Platform=x64` 才能命中最新建置；驗證結果 0 警告 0 錯誤、87 項單元測試全數通過。

### 待辦／已知限制
- 待於實體高更新率 Android 裝置與 Wi-Fi 環境實測追幀延遲與 WASAPI 環形緩衝區表現。

---

## 2026-11-26 - v1.4.2

### 目標
執行 8 項低改動、高收益的穩定性、效能與桌面體驗優化。

### 變更
- `src/AndroidSync/Helpers/ScreenshotHelper.cs`：`Clipboard.SetContent` 後補呼叫 `Clipboard.Flush()`，避免 `InMemoryRandomAccessStream` 提早釋放導致貼上失敗。
- `src/AndroidSync/Views/MirrorPage.xaml.cs`：
  - `PointerCanceled` 補送 `AndroidMotionAction.Up`（含多點觸控與雙指縮放模式），防止拖曳中斷時手機觸控卡住。
  - `PointerMoved` 加入同座標去重與 `4ms` 節流。
  - `ShowToast` 加入可取消自動關閉計時（3 秒／錯誤 5 秒），傳檔進行中維持顯示直到完成。
  - `KeyboardInputHost_PreviewKeyDown` 新增 `Alt+O/S/F/T/M/R` 桌面快捷鍵。
- `src/AndroidSync/Views/MainWindow.xaml.cs`：`FitWindowToAspectRatio` 依 `XamlRoot.RasterizationScale` 換算邊框與最小尺寸（抽離 `ComputeFittedWindowSize` 供測試）。
- `src/AndroidSync/Protocol/ScrcpyAudioPacketView.cs`、`ScrcpyProtocolClient.cs`、`Services/MirrorSessionService.cs`：
  - 新增 `ReadNextAudioPacketViewAsync` 重用 `_audioPayloadBuffer`，AAC 路徑零配置、RAW 靜音略過拷貝。
  - 新增 `scrcpy-server` 推送快取（依裝置序號與檔案大小／時間戳），連線失敗時自動失效重推。
- `src/AndroidSync/Rendering/D3D11SwapChainRenderer.cs`：移除每幀 `ContextCopyResource` 備份，改為截圖直讀 `_cachedBackBuffer`、縮放時按需備份，並快取 Video Processor 矩形狀態。
- `tests/AndroidSync.Tests/V14OptimizationTests.cs`：新增高 DPI 比例貼合與音訊緩衝區重用單元測試。
- `Directory.Build.props`、`CHANGELOG.md`：遞增版本至 `1.4.2`。

### 問題與解法
- 測試專案存取 `MainWindow.ComputeFittedWindowSize` 時因存取修飾詞為 `internal` 編譯失敗 → 調整為 `public static`，全專案 0 警告 0 錯誤且 84 項單元測試全數通過。

### 待辦／已知限制
- 待於實體高 DPI 螢幕與 Android 裝置驗證 `Alt+` 快速鍵、截圖貼上與按需備份縮放表現。

---

## 2026-11-26 - v1.4.1

### 目標
1. 縮小畫面同步頁右側工具列按鈕與間距，減少畫面空間佔用並避免一般視窗高度下溢位捲動。
2. 修正深色模式下視窗右上角標題列按鈕（最小化／最大化／關閉）不可見問題。

### 變更
- `src/AndroidSync/Styles/AppStyles.xaml`、`Views/MirrorPage.xaml`、`Views/MainWindow.xaml.cs`：
  - `AppToolbarIconButtonStyle` 由 `44x44`（圓角 8）縮小為 `32x32`（`MinWidth/MinHeight=0`、圓角 6），工具列 `FontIcon` 統一縮小為 `FontSize="14"`，外框內距與按鈕間距調整為 `4`，`MenuFlyoutSeparator` 改為精簡 `1px` 分隔線（`AppToolbarSeparatorStyle`）；工具列總高度由約 820 DIP 降至約 580 DIP，寬度由 56 DIP 降至 40 DIP，同步更新 `FitWindowToAspectRatio` 的 `chromeWidth`。
- `src/AndroidSync/Services/ThemeService.cs`、`tests/AndroidSync.Tests/ThemeServiceTests.cs`：
  - `UpdateTitleBarColors` 補齊 `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)` 與完整標題列色彩（`BackgroundColor`、`InactiveBackgroundColor`、`ForegroundColor`、`InactiveForegroundColor`、`ButtonBackgroundColor`、`ButtonInactiveBackgroundColor` 及不透明 hover／pressed 背景色），並優先讀取當前主題的 `AppWindowBackgroundBrush` 與透過 `UISettings` 判斷系統深色模式。
  - 新增 `ThemeServiceTests` 驗證深淺模式標題列前後景對比與不透明度。
- `Directory.Build.props`、`CHANGELOG.md`：遞增版本至 `1.4.1`。

### 問題與解法
- 系統為淺色模式、App 切為深色模式時，原生標題列背景維持白色而 `ThemeService` 僅將 `ButtonForegroundColor` 設為白色，導致右上角按鈕「白底白字」不可見 → 同時設定 DWM 深色模式屬性與完整 `AppWindowTitleBar` 背景／按鈕背景色解決。

### 待辦／已知限制
- 待於實體 Windows 11 視窗確認深淺色與五款主題切換時標題列視覺一致性。

---

## 2026-11-26 - v1.4.0

### 目標
執行全面底層管線穩定性修復與進階桌面功能優化（P0–P2 全項目）。

### 變更
- **底層穩定性與效能管線**：
  - `Views/MainWindow.xaml.cs`：修正 `OnWindowClosing` 非同步關閉競態（先 `args.Cancel = true` 等待 `SaveAsync` 與 `StopAsync` 完成再關閉）；新增視窗置頂（`IsAlwaysOnTop`）、精簡浮動模式與自動貼合手機比例（`FitWindowToAspectRatio`）。
  - `Rendering/MediaFoundationH264Decoder.cs`：新增 `DecodeFrames` 完整輸出排空迴圈與 `MF_E_NOTACCEPTING` 重試，並透過 `MFTEnumEx` 支援 H.265 (HEVC) 解碼（系統未安裝時自動退回 H.264）。
  - `Rendering/D3D11SwapChainRenderer.cs`：新增 `DXGI_ERROR_DEVICE_REMOVED / RESET` 偵測與 `RecoverDevice` 重建、快取 `ID3D11VideoProcessorOutputView`、追蹤 `CompositionScaleX/Y`，並實作 GPU staging 紋理即時截圖 `TryCaptureCurrentFrameBgra`。
  - `Protocol/PooledVideoFrame.cs`、`ScrcpyProtocolClient.cs`、`Services/MirrorSessionService.cs`：以 `ArrayPool<byte>` 與可重用緩衝區降低 GC 配置，將視訊網路讀取與解碼解耦為有界 `Channel`，新增效能 HUD 統計、指數退避自動重連與拖放傳檔／APK 安裝。
- **音訊與 ADB 升級**：
  - `Audio/WasapiAudioPlayer.cs`、`MediaFoundationAacDecoder.cs`：實作零依賴 WASAPI 低延遲共享模式播放（含預設音訊裝置變更自動切換，失敗退回 `WaveOutAudioPlayer`）與 Media Foundation AAC 解碼。
  - `Services/AdbSmartSocketClient.cs`、`AdbService.cs`、`DeviceManagerService.cs`、`Views/DeviceListPage.xaml.cs`：直連 ADB daemon `127.0.0.1:5037` 支援 `host:devices-l` 與 `host:track-devices-l` 即時裝置追蹤，並快取工具路徑。
- **進階操控與介面**：
  - `Helpers/PinchGestureCalculator.cs`、`ScreenshotHelper.cs`、`Protocol/ScrcpyControlSerializer.cs`、`Views/MirrorPage.xaml(.cs)`、`ViewModels/MirrorViewModel.cs`、`Views/SettingsPage.xaml`、`ViewModels/SettingsViewModel.cs`、`Strings/zh-Hant/Resources.resw`：新增拖放傳檔／APK 安裝、一鍵截圖、置頂、精簡模式、貼合比例、效能 HUD、`Ctrl + 拖曳` 模擬雙指縮放、觸控螢幕／手寫筆多點透傳與 H.265／AAC／自動重連／HUD 設定。
- **測試與文件**：
  - `tests/AndroidSync.Tests/`：新增 `PinchGestureCalculatorTests`、`AdbSmartSocketClientTests`、`V14OptimizationTests`。
  - 更新 `Directory.Build.props` 至 `1.4.0` 並同步 `README.md`、`CHANGELOG.md`、`RELEASE_NOTES.md`。

### 問題與解法
- 沙盒環境執行 `dotnet build` 會因讀取使用者目錄 `NuGet.Config` 遭拒而失敗，且多節點並行 XAML 編譯易互相取消 → 改用 `dotnet build --no-restore -m:1` 驗證，0 警告 0 錯誤通過。

### 待辦／已知限制
- 待於實體 Android 裝置回歸驗證 H.265 硬解、AAC+WASAPI 音訊裝置熱切換、拖放 APK 安裝與 `Ctrl + 拖曳` 雙指縮放。

---

## 2026-11-26 - v1.3.0

### 目標
1. 實作 Media Foundation DXVA + Direct3D 11 Video Processor GPU 零拷貝渲染管線。
2. 實作顯示模式（跟隨系統／淺色／深色）與五款主題（經典藍、黃色護眼、森林綠、暮色紫、中性灰）即時切換，以及單一實例（Mutex + 喚醒既有視窗）。

### 變更
- `src/AndroidSync/Rendering/DecodedVideoFrame.cs`、`MediaFoundationH264Decoder.cs`、`D3D11SwapChainRenderer.cs`、`Services/IMirrorSessionService.cs`、`MirrorSessionService.cs`：
  - `D3D11SwapChainRenderer` 改為共用單一 `ID3D11Device`（`BGRA_SUPPORT | VIDEO_SUPPORT` + `ID3D10Multithread`），建立 `IMFDXGIDeviceManager` 與 `ID3D11VideoDevice`/`ID3D11VideoContext`；新增 `ID3D11VideoProcessor` GPU NV12→BGRA 裁切轉換與 GPU-to-GPU 備份紋理供視窗縮放重繪。
  - `MediaFoundationH264Decoder` 支援注入 `IMFDXGIDeviceManager`（`MFT_MESSAGE_SET_D3D_MANAGER` + `MF_LOW_LATENCY`），於硬體解碼時直接取出 `IMFDXGIBuffer` 的 `ID3D11Texture2D` 與 `SubresourceIndex` 並於單幀渲染結束後即時釋放 `IMFSample`（避免 DXVA surface pool 耗盡）；環境不支援時自動降級為 CPU NV12→BGRA。
- `src/AndroidSync/Models/AppDisplayMode.cs`、`AppColorTheme.cs`、`AppSettings.cs`、`Styles/Themes/*.xaml`、`Styles/AppStyles.xaml`、`Services/IThemeService.cs`、`ThemeService.cs`、`ViewModels/SettingsViewModel.cs`、`Views/SettingsPage.xaml`、`Views/MainWindow.xaml`、`Views/MirrorPage.xaml`、`Strings/zh-Hant/Resources.resw`：
  - 新增五款支援淺色與深色模式的 `Styles/Themes/` 資源字典；`ThemeService` 支援切換 `ElementTheme`、`UISettings.ColorValuesChanged` 系統主題同步、即時替換主題資源字典並觸發視覺樹重算與標題列按鈕色彩更新。
- `src/AndroidSync/Services/SingleInstanceGuard.cs`、`Program.cs`、`AndroidSync.csproj`、`App.xaml.cs`、`Views/MainWindow.xaml.cs`：
  - 啟用 `DISABLE_XAML_GENERATED_MAIN` 並於 `Program.Main` 檢查具名 `Mutex`；次實例呼叫 `AllowSetForegroundWindow(ASFW_ANY)` 並透過具名 `EventWaitHandle` 喚醒主實例，將既有視窗還原並帶回前景。
- `tests/AndroidSync.Tests/`：新增 `ThemeServiceTests`、`SingleInstanceGuardTests`，並擴充 `D3D11SwapChainRendererTests` 與 `MediaFoundationH264DecoderTests`。
- `Directory.Build.props`、`CHANGELOG.md`、`RELEASE_NOTES.md`：更新版本至 `1.3.0`。

### 問題與解法
- DXVA H.264 MFT 的硬體輸出表面池數量有限，若不於每幀渲染完畢後立即釋放 `IMFSample` 會導致後續 `ProcessOutput` 阻塞 → 於 `MirrorSessionService.VideoDecodeLoopAsync` 的 `finally` 區塊呼叫 `_decoder.ReleaseOutputFrame()`，確保 `VideoProcessorBlt` 送出後立即歸還表面。
- WinUI 3 動態替換 `Application.Current.Resources.MergedDictionaries` 後既有視覺樹的 `{ThemeResource}` 不會自動重算 → 替換資源字典後於 `ThemeService.ForceVisualTreeThemeRefresh()` 短暫切換 `Window.Content` 的 `RequestedTheme` 再還原目標模式，達成零閃爍即時套用。

### 待辦／已知限制
- 待於實體 Android 裝置驗證 GPU 零拷貝渲染延遲與視窗縮放表現，以及第二次啟動執行檔時前景喚醒既有視窗。

---

## 2026-11-26 - v1.2.0

### 目標
實作第二階段音訊同步功能：透過 `scrcpy-server` 串流 Android 11+ 裝置音訊並於 Windows 即時低延遲播放。

### 變更
- `src/AndroidSync/Protocol/ScrcpyAudioPacket.cs`、`ScrcpyProtocolClient.cs`：新增音訊 Socket 連線（依 scrcpy 順序：Video → Audio → Control）、4 byte `AudioCodecId` 握手（`0x00726177` raw PCM；若裝置回傳 `0`/`1` 則自動降級維持純畫面同步）與 `ReadNextAudioPacketAsync`（相容 v2/v3 與 v5.0+ 封包旗標）。
- `src/AndroidSync/Audio/IAudioPlayer.cs`、`WaveOutAudioPlayer.cs`：實作零第三方依賴的 Windows 原生 `winmm`（`waveOut*`）48kHz 16-bit 立體聲 PCM 播放器，採用非受控固定記憶體 `WAVEHDR` 環形槽位、`CALLBACK_EVENT` 喚醒與有界佇列（`DropOldest`）控制延遲，並支援即時靜音。
- `src/AndroidSync/Services/IMirrorSessionService.cs`、`MirrorSessionService.cs`：新增 `audio=true audio_codec=raw` 啟動參數、獨立音訊播放背景迴圈、`IsAudioActive`/`IsAudioMuted` 狀態與 `ToggleAudioMute()`。
- `src/AndroidSync/Models/MirrorOptions.cs`、`ViewModels/MirrorViewModel.cs`、`Views/MirrorPage.xaml`、`ViewModels/SettingsViewModel.cs`、`Views/SettingsPage.xaml`、`Strings/zh-Hant/Resources.resw`：新增「同步播放手機音訊」設定開關與右側快捷列靜音按鈕。
- `tests/AndroidSync.Tests/ScrcpyProtocolClientTests.cs`、`WaveOutAudioPlayerTests.cs`：新增音訊握手、RAW PCM 封包讀取、裝置停用音訊自動降級與播放器生命週期單元測試。
- `Directory.Build.props`、`CHANGELOG.md`：更新版本至 `1.2.0`。

### 問題與解法
- `waveOutPrepareHeader`／`waveOutWrite` 會於非同步播放期間持續引用 `WAVEHDR` 記憶體位址，若直接傳遞受控物件欄位可能於 GC 壓縮時失效 → 改以 `NativeMemory.AllocZeroed` 配置固定非受控 `WAVEHDR` 陣列搭配 `GCHandleType.Pinned` 鎖定 PCM 緩衝區。

### 待辦／已知限制
- 待於實體 Android 11+ 手機實測鍵盤輸入、雙向剪貼簿、音訊同步延遲與靜音切換。
- 螢幕錄影功能已確認不納入規劃。

---

## 2026-11-26 - v1.1.0

### 目標
實作第二階段首項功能：電腦鍵盤輸入（含中文 IME／Emoji）與電腦／手機雙向剪貼簿同步。

### 變更
- `src/AndroidSync/Protocol/`：擴充 `AndroidKeyCode` 完整鍵碼、新增 `AndroidMetaState` 與 `ScrcpyClipboardCopyKey`；`ScrcpyControlSerializer` 新增 `TYPE_INJECT_TEXT`、`TYPE_GET_CLIPBOARD`、`TYPE_SET_CLIPBOARD` 封包序列化（含 UTF-8 字元邊界安全截斷）；`ScrcpyProtocolClient` 新增背景控制通道讀取迴圈解析裝置端 `TYPE_CLIPBOARD` 與 `TYPE_ACK_CLIPBOARD` 訊息。
- `src/AndroidSync/Helpers/KeyboardMapper.cs`、`ClipboardSyncCoordinator.cs`：新增 Windows `VirtualKey` 與修飾鍵至 Android 鍵碼映射工具，以及防迴圈與防 IME 覆寫電腦剪貼簿的雙向剪貼簿協調器。
- `src/AndroidSync/Services/IMirrorSessionService.cs`、`MirrorSessionService.cs`：新增 `SendKey`、`SendText`（ASCII 走 `INJECT_TEXT`、非 ASCII／IME 走 `SET_CLIPBOARD` 貼上）、`SyncHostClipboardToDevice`、`RequestDeviceClipboard` 與 `DeviceClipboardChanged` 事件，並於啟動參數傳入 `clipboard_autosync`。
- `src/AndroidSync/Views/MirrorPage.xaml`、`MirrorPage.xaml.cs`：以可聚焦 `ContentControl`（`KeyboardInputHost`）承接 `PreviewKeyDown`/`PreviewKeyUp`/`CharacterReceived`（含 UTF-16 surrogate pair）與 `Ctrl+V` 同步貼上，並掛載 Windows `Clipboard.ContentChanged` 實作雙向同步。
- `src/AndroidSync/Models/MirrorOptions.cs`、`ViewModels/SettingsViewModel.cs`、`Views/SettingsPage.xaml`、`Strings/zh-Hant/Resources.resw`：新增「啟用電腦與手機雙向剪貼簿同步」設定開關。
- `tests/AndroidSync.Tests/`：新增 `KeyboardMapperTests`、`ClipboardSyncCoordinatorTests`，並補齊 `ScrcpyControlSerializerTests` 與 `ScrcpyProtocolClientTests` 的鍵盤與剪貼簿協議測試。
- `Directory.Build.props`、`CHANGELOG.md`：更新版本至 `1.1.0`。

### 問題與解法
- `SwapChainPanel` 非 `Control` 子類別、預設無法取得鍵盤焦點 → 以 `IsTabStop="True"` 的 `ContentControl` 包覆視訊容器並於點擊畫面／連線成功時取得焦點，使用 `PreviewKeyDown` 避免 `NavigationView` 攔截方向鍵與 Tab。
- scrcpy `INJECT_TEXT` 僅支援 ASCII，中文 IME 輸入無法直接送出 → 於 `SendText` 偵測非 ASCII 字元改走 `SET_CLIPBOARD(paste: true)`，並在 `ClipboardSyncCoordinator` 記錄 IME 注入文字，避免裝置端自動回推覆寫使用者原本的 Windows 剪貼簿。

### 待辦／已知限制
- 待於實體 Android 手機實測中英文鍵盤輸入、組合鍵與雙向剪貼簿同步。
- 第二階段剩餘項目：音訊同步、螢幕錄影。

---

## 2026-11-26 - v1.0.5

### 目標
修正畫面同步時，電腦畫面右側與下方邊緣出現綠色邊框的問題。

### 變更
- `src/AndroidSync/Rendering/MediaFoundationH264Decoder.cs`：新增 `MF_MT_MINIMUM_DISPLAY_APERTURE`／`MF_MT_GEOMETRIC_APERTURE` 讀取（`IMFAttributes::GetBlob` 與 `IMFTransform::GetOutputCurrentType`），區分編碼尺寸（`_codedWidth`/`_codedHeight`）與實際可視裁切區域（`_cropX`/`_cropY`/`_width`/`_height`），避免輸出 H.264 巨集區塊補齊像素。
- `src/AndroidSync/Rendering/Nv12ColorConverter.cs`：新增支援 `codedHeight`、`cropX`、`cropY` 的多載，確保以編碼高度計算 UV 平面偏移並僅轉換可視區域像素。
- `tests/AndroidSync.Tests/Nv12ColorConverterTests.cs`、`tests/AndroidSync.Tests/MediaFoundationH264DecoderTests.cs`：新增右／下補齊裁切去綠邊與 aperture 邊界驗證測試。
- `Directory.Build.props`、`CHANGELOG.md`：更新版本至 `1.0.5`。

### 問題與解法
- H.264 解碼輸出採 16×16 巨集區塊對齊，`MF_MT_FRAME_SIZE` 回報的是補齊後的編碼尺寸（如 1088×2352），右／下補齊區於 NV12 中為 `Y=0, U=0, V=0`，轉為 BGRA 會變成亮綠色（`RGB(0, 135, 0)`）→ 改由輸出媒體型別讀取可視顯示區域（`MF_MT_MINIMUM_DISPLAY_APERTURE`／`MF_MT_GEOMETRIC_APERTURE`），保留 `codedHeight` 作為 NV12 UV 平面偏移依據，並僅輸出可視區域 BGRA 影像。

### 待辦／已知限制
- 待於實體 Android 手機驗證右側與下方綠邊已完全消除。

---

## 2026-11-26 - v1.0.4

### 目標
修正連線成功並出現畫面後，只顯示手機畫面左上角（未縮放至視窗區域）的問題。

### 變更
- `src/AndroidSync/Rendering/D3D11SwapChainRenderer.cs`：透過 `IDXGISwapChain2::SetMatrixTransform` 依 `displayWidth / bufferWidth` 與 `displayHeight / bufferHeight` 套用 2D 縮放矩陣；新增 `UpdateDisplaySize` 與 `ComputeScaleFactor`，並快取最後一幀 BGRA 資料以於視窗縮放時即時重繪。
- `src/AndroidSync/Views/MirrorPage.xaml.cs`：於 `UpdatePanelLayout()` 扣除容器邊框後呼叫 `_renderer.UpdateDisplaySize(rect.Width, rect.Height)` 同步顯示尺寸。
- `tests/AndroidSync.Tests/D3D11SwapChainRendererTests.cs`：新增縮放比例計算與 `IDXGISwapChain2::SetMatrixTransform` 整合測試。
- `Directory.Build.props`、`CHANGELOG.md`：更新版本至 `1.0.4`。

### 問題與解法
- `CreateSwapChainForComposition` 建立的 SwapChain 在 `SwapChainPanel` 上預設採 1:1 像素對應 DIP，`DXGI_SCALING_STRETCH` 不會自動縮放至面板尺寸，導致高解析度（如 1080×2400）畫面超出面板邊界而僅見左上角 → 透過 `IDXGISwapChain2::SetMatrixTransform` 同步設定縮放矩陣，並於 `Attach`、`ResizeSwapChain` 與 `UpdateDisplaySize` 時更新。

### 待辦／已知限制
- 待於實體 Android 手機驗證全畫面等比例顯示、視窗拖拉縮放與滑鼠點擊座標對齊。

---

## 2026-11-26 - v1.0.3

### 目標
修正連線 `scrcpy-server` v5.0.1 時出現 `Media Foundation SetInputType 失敗 (0xC00D36B4)` 的錯誤。

### 變更
- `src/AndroidSync/Protocol/ScrcpyProtocolClient.cs`：支援 `scrcpy-server` v5.0+ 新串流格式（4 byte `codecId` + 12 byte `writeSessionMeta` `[0x80000000/0x80000001, width, height]`），並同步更新 v5 封包旗標位元（`PACKET_FLAG_SESSION = bit 63`、`CONFIG = bit 62`、`KEY_FRAME = bit 61`）與串流中途旋轉／尺寸變更處理，同時保留對 v2/v3 協議的自動相容。
- `src/AndroidSync/Rendering/MediaFoundationH264Decoder.cs`：僅於 `width > 0 && height > 0` 時設定 `MF_MT_FRAME_SIZE`。
- `tests/AndroidSync.Tests/ScrcpyProtocolClientTests.cs`、`tests/AndroidSync.Tests/MediaFoundationH264DecoderTests.cs`：新增 v5 協議握手、封包旗標、串流中途 session meta 與 H.264 解碼器初始化測試。
- `Directory.Build.props`、`CHANGELOG.md`：更新版本至 `1.0.3`。

### 問題與解法
- 反組譯 `Tools/scrcpy-server`（v5.0.1）`Streamer.class` 發現：`writeVideoHeader()` 由 12 byte 縮減為 4 byte（僅 `codecId`），寬高改由緊接其後的 12 byte `writeSessionMeta` 傳送；舊版解析程式誤將 `0x80000000`（`-2147483648`）當成 `width` 傳給 Media Foundation，觸發 `MF_E_INVALIDMEDIATYPE (0xC00D36B4)` → 改依高 bit 自動辨識 v5 與 v2/v3 格式並修正旗標位元。

### 待辦／已知限制
- 待於實體 Android 手機驗證完整畫面顯示與操作。

---

## 2026-11-26 - v1.0.2

### 目標
修正啟動畫面同步時出現「scrcpy 串流已關閉。」錯誤的問題。

### 變更
- `src/AndroidSync/Protocol/ScrcpyProtocolClient.cs`：將 `adb forward` 模式的 TCP 連線與讀取 1 byte dummy byte 合併進重試迴圈，並於 `scrcpy-server` 提前結束時回報實際錯誤訊息。
- `src/AndroidSync/Services/MirrorSessionService.cs`：移除 `scrcpy-server` v5.0.1 已移除的 `send_codec_meta=true` 參數（各版本預設皆為啟用）；新增 `scrcpy-server` stdout／stderr 非同步讀取與日誌緩衝，避免管線阻塞並提供錯誤診斷資訊。
- `tests/AndroidSync.Tests/ScrcpyProtocolClientTests.cs`：新增 `adb forward` 提早 EOF 重試與伺服器異常結束回報的單元測試。
- `Directory.Build.props`、`CHANGELOG.md`：更新版本至 `1.0.2`。

### 問題與解法
- `adb forward` 會在 `scrcpy-server` 尚未啟動監聽前就接受本機 TCP 連線並立即關閉（EOF），原本只在 `ConnectAsync` 重試、未把讀取 dummy byte 納入重試 → 改為將連線與讀取 dummy byte 一起重試。
- `scrcpy-server` v5.0.1 將 `send_codec_meta` 改名為 `send_stream_meta`，傳入舊參數會觸發 `Unknown server option` 導致伺服器直接結束 → 移除該多餘參數（預設即會送出串流中繼資料，同時相容 v2/v3/v5）。
- 原本未讀取 `scrcpy-server` 行程的 stdout／stderr，導致看不到真實錯誤原因 → 掛載 `OutputDataReceived`/`ErrorDataReceived` 並於連線失敗時附帶輸出內容。

### 待辦／已知限制
- 待使用者於實體 Android 手機再次驗證畫面同步。

---

## 2026-11-26 - v1.0.1

### 目標
將預設 `scrcpy-server` 協議對齊至最新官方版本 `v5.0.1`，並於設定頁提供版本號調整欄位。

### 變更
- `src/AndroidSync/Models/MirrorOptions.cs`：預設 `ScrcpyServerVersion` 由 `2.7` 更新為 `5.0.1`。
- `src/AndroidSync/Protocol/ScrcpyControlSerializer.cs`：依 v3.0+／v5.0.1 協議將 `TYPE_SET_DISPLAY_POWER`（type 10）開啟值由 `2` 更新為 `1`。
- `src/AndroidSync/ViewModels/SettingsViewModel.cs`、`Views/SettingsPage.xaml`、`Strings/zh-Hant/Resources.resw`：新增 `scrcpy-server` 版本號設定欄位。
- `tests/AndroidSync.Tests/ScrcpyControlSerializerTests.cs`：更新顯示電源控制封包單元測試。
- `Directory.Build.props`、`CHANGELOG.md`、`README.md`：更新版本至 `1.0.1`。

### 問題與解法
- v3.0+ 之後 `SET_SCREEN_POWER_MODE` 改為 `SET_DISPLAY_POWER`（boolean `0`/`1`），已同步修正控制封包格式。

### 待辦／已知限制
- 待放置 `scrcpy-server` v5.0.1 後以實體 Android 手機驗證連線。

---

## 2026-11-26 - v1.0.0

### 目標
完成「Android同步」（`Com.AndroidSync.App`）第一階段 MVP：.NET 10 + WinUI 3 專案骨架、USB／Wi-Fi 連線管理、`scrcpy-server` 協議與 Media Foundation H.264 + Direct3D 11 `SwapChainPanel` 原生渲染、滑鼠控制與側邊快捷列、App 圖示與視窗狀態記憶。

### 變更
- `AGENTS.md`、`docs/SPEC.md`、`README.md`、`CHANGELOG.md`、`RELEASE_NOTES.md`：補齊專案規格與說明文件。
- `Directory.Build.props`、`Directory.Packages.props`、`AndroidSync.sln`：建立 .NET 10（Win11 x64）與 Central Package Management 組態。
- `src/AndroidSync/Services/`：實作 `AdbService`、`DeviceManagerService`、`MirrorSessionService`、`SettingsService`、`WindowStateService`。
- `src/AndroidSync/Protocol/`、`src/AndroidSync/Helpers/`：實作 `ScrcpyProtocolClient`、`ScrcpyControlSerializer`、`CoordinateMapper`。
- `src/AndroidSync/Rendering/`：實作 `MediaFoundationH264Decoder`、`Nv12ColorConverter`、`D3D11SwapChainRenderer`。
- `src/AndroidSync/Views/`、`ViewModels/`、`Styles/`、`Strings/zh-Hant/`、`Assets/`：建立 WinUI 3 頁面、側邊快捷列、繁體中文資源與 App 圖示。
- `tests/AndroidSync.Tests/`：新增 ADB 解析、座標映射、控制封包序列化與 NV12 轉色單元測試。

### 問題與解法
- `TargetPlatformMinVersion=10.0.22000.0` 搭配 `net10.0-windows10.0.19041.0` 觸發 `NETSDK1135`（`SupportedOSPlatformVersion` 不得高於 `TargetPlatformVersion`）→ 將 `Directory.Build.props` 的 `TargetFramework` 調整為 `net10.0-windows10.0.22000.0`，建置 0 警告 0 錯誤通過。

### 待辦／已知限制
- 執行時需於 `Tools/scrcpy-server` 放置 `scrcpy-server` 檔案（或於設定頁指定路徑），尚未自動下載第三方二進位檔。
- 尚未於實體 Android 手機完成端對端連線實測；第二階段規劃鍵盤輸入、剪貼簿、音訊同步與錄影。

---

## 2026-11-23 - v1.0.0

### 目標
調整共用開發規範：UI 改為 WinUI 3／WPF 二選一、App 圖示與視窗狀態列為必做、平台提高為 Windows 11 以上。

### 變更
- `AGENTS.md`：技術基線 UI 改為 WinUI 3（建議）／WPF 二選一，平台改 Windows 11（build 22000）以上。
- `AGENTS.md`：專案結構、程式碼規範、共用界面補上 WPF 對照（.resx、DynamicResource、NavigationView 替代、WindowChrome 等）。
- `AGENTS.md`：第 5 章拆為「必要／選用」，App 圖示與視窗狀態改為必做並補兩框架作法。
- `AGENTS.md`：`TargetPlatformMinVersion` 改 `10.0.22000.0`，TFM 維持 `net10.0-windows10.0.19041.0`；發佈指令分 WinUI 3／WPF。
- `AGENTS.md`：專案專屬補充新增 UI 框架與最低平台欄位。

### 問題與解法
- 無。

### 待辦／已知限制
- 尚未建立實際專案驗證兩框架範本。
