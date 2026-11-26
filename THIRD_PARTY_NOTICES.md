# 第三方元件授權通知

「Android同步」本體以 MIT License 釋出。本專案使用或散布以下第三方元件，其授權如下。

## scrcpy / scrcpy-server

- 專案：https://github.com/Genymobile/scrcpy
- 作者：Genymobile 與 scrcpy 貢獻者
- 授權：Apache License 2.0
- 用途：本專案內附 `src/AndroidSync/Tools/scrcpy-server`，於 Android 裝置上執行以提供畫面與音訊串流及控制通道。
- 授權全文：https://www.apache.org/licenses/LICENSE-2.0

使用或重散布 `scrcpy-server` 時，請保留其著作權與授權聲明。本專案未修改 `scrcpy-server` 二進位檔。

## Android Debug Bridge（adb）

- 專案：https://developer.android.com/tools/adb
- 授權：Apache License 2.0（Android SDK Platform Tools）
- 用途：與 Android 裝置通訊。
- 說明：`adb.exe` **未**隨本專案散布，需由使用者自行安裝 Android SDK Platform Tools。

## .NET 與 Windows App SDK

- .NET Runtime／SDK：MIT License（https://github.com/dotnet/runtime）
- Windows App SDK：Microsoft 授權條款（https://learn.microsoft.com/windows/apps/windows-app-sdk/）
- Windows SDK Build Tools：Microsoft 授權條款

## CommunityToolkit.Mvvm

- 專案：https://github.com/CommunityToolkit/dotnet
- 授權：MIT License
- 用途：MVVM 的 `ObservableObject` 與 `RelayCommand`。

## Microsoft.Extensions.Hosting／DependencyInjection／Logging

- 專案：https://github.com/dotnet/runtime
- 授權：MIT License
- 用途：應用程式啟動、依賴注入與日誌抽象層。

---

若你是權利人並認為本專案的使用方式需要調整，請透過 [Issues](https://github.com/mark216tw/android-sync/issues) 或 **mark216tw@gmail.com** 與我們聯絡。
