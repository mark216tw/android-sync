# 貢獻指南

感謝你願意協助改善「Android同步」。本文件說明提交問題與程式碼變更的流程與規範。

## 回報問題

1. 先搜尋既有 [Issues](https://github.com/mark216tw/android-sync/issues)，避免重複。
2. 建立 Issue 時請附上：
   - Windows 版本與組建號碼（`winver`）
   - 應用程式版本（「關於」或 `AndroidSync.exe` 檔案版本）
   - 手機型號、Android 版本、連線方式（USB／Wi-Fi）
   - 重現步驟、預期結果與實際結果
   - 相關錯誤訊息或日誌片段（请勿包含個人隱私資料）
3. 涉及安全性的問題請勿公開張貼，請依 [SECURITY.md](SECURITY.md) 回報。

## 開發流程

```powershell
git clone https://github.com/mark216tw/android-sync.git
cd android-sync
dotnet build AndroidSync.sln
dotnet test AndroidSync.sln -p:Platform=x64
```

- 請以 `Debug` 組態開發與測試。
- 功能變更完成後，務必執行 `dotnet build` 與 `dotnet test`，確認 0 警告、0 錯誤。
- 若專案提供格式檢查，請執行 `dotnet format --verify-no-changes`。

## 程式碼規範

- 使用 C# 最新語法、file-scoped namespace、nullable reference types。
- 4 空格縮排；公開型別與檔名一致，一個檔案一個公開型別。
- 非同步方法以 `Async` 結尾並回傳 `Task`／`Task<T>`；函式庫層加 `ConfigureAwait(false)`。
- 不在 UI 執行緒進行 I/O；長時間工作改用非同步流程或背景執行緒。
- 使用 `Microsoft.Extensions.DependencyInjection` 註冊服務，不要在 View 直接 `new` 服務。
- 使用者可見文字放入 `src/AndroidSync/Strings/zh-Hant/Resources.resw`，預設繁體中文。
- 不要留下 `TODO`、測試假資料或除錯輸出。
- 不要引入未在 `Directory.Packages.props` 宣告的套件。

## 提交訊息

請使用簡潔、可理解的訊息，建議格式：

```
類型: 簡短說明

- 變更重點
- 變更重點
```

類型範例：`feat`、`fix`、`perf`、`docs`、`refactor`、`test`、`build`、`chore`。

## Pull Request

1. 從 `main` 建立功能分支。
2. 確認變更範圍聚焦，避免混合無關修改。
3. 更新相關文件（`README.md`、`docs/`、`CHANGELOG.md`、`docs/DEV_LOG.md`）。
4. 確認建置與測試通過，並在 PR 描述中說明動機、做法與驗證方式。

## 授權

送出貢獻即表示你同意以本專案的 [MIT License](LICENSE) 授權你的貢獻。
