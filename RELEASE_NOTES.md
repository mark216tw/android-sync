# Android同步 v1.4.3 發行說明

本次發行整合 1.4.x 系列的最佳化與修正，重點在於更低延遲的視訊／音訊管線、更穩定的連線與更順手的桌面操作體驗。

## 下載

| 系統架構 | 檔案 |
| --- | --- |
| x64（Intel／AMD） | `AndroidSync-1.4.3-win-x64-non-self-contained.zip` |
| ARM64 | `AndroidSync-1.4.3-win-arm64-non-self-contained.zip` |

> 本版本為**非 Self-Contained（framework-dependent）**，執行前請安裝：
> - [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
> - [Windows App Runtime 1.8](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
>
> 另需自備 Android Platform Tools（`adb.exe`）。`scrcpy-server` 已內附。

## 主要更新

### 更低的延遲與更少的資源占用
- 視訊解碼加入積壓追幀策略，快速消除 Wi-Fi 抖動或視窗縮放造成的延遲累積。
- D3D11 交換鏈啟用可等待物件與最大呈現延遲 1，並快取 Video Processor 輸入／輸出 View。
- NV12 轉 BGRA 改採雙像素共用 UV 與大畫面並行轉換。
- 音訊改用固定容量環形緩衝，並將 PCM 寫入升級為零配置 `Span`；AAC 解碼重用原生緩衝。
- ADB Smart Socket 優先直連轉發連接埠，並擴大視訊接收緩衝區、合併控制封包寫入。

### 更穩定的連線與渲染
- 修正 D3D11 DeviceLost 事件可能造成的死鎖風險。
- 串流中斷或裝置遺失時，以指數退避自動重連。
- 修正關閉視窗時的非同步競態，確保狀態儲存與程序清理完成。

### 更順手的桌面體驗
- H.265 (HEVC) 硬體解碼與可切換效能 HUD。
- AAC 壓縮音訊與 WASAPI 低延遲播放（自動跟隨預設音訊裝置）。
- 一鍵 GPU 截圖、視窗置頂、精簡浮動模式、貼合手機比例。
- 拖放檔案與 `.apk` 一鍵安裝。
- `Ctrl + 拖曳` 雙指縮放、觸控螢幕／手寫筆多點透傳。
- 桌面快捷鍵：`Alt+O/S/F/T/M/R`。
- 顯示模式與五款主題即時切換。
- 高 DPI 比例貼合修正、深色模式標題列按鈕可見性修正。

## 已知限制

- 需自行安裝 `adb.exe` 與上述執行期元件。
- 無線連線品質受區域網路影響；延遲較高時可降低解析度、位元率或 FPS。
- 部分 Android 客製系統可能限制音訊擷取或無線偵錯，程式會自動降級。
- 未提供安裝程式，僅提供 ZIP。

完整技術異動請見 [CHANGELOG.md](CHANGELOG.md)，使用說明請見 [docs/USAGE.md](docs/USAGE.md)。
