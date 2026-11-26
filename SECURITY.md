# 安全性政策

## 支援版本

本專案以最新發行版本提供安全性修正。建議隨時使用 [Releases](https://github.com/mark216tw/android-sync/releases) 中的最新版本。

## 回報安全性問題

若發現潛在安全性問題，請**不要**建立公開 Issue。請透過以下方式私下回報：

- 使用 GitHub 的 [Private vulnerability reporting](https://github.com/mark216tw/android-sync/security/advisories/new)，或
- 寄送電子郵件至 **mark216tw@gmail.com**

請盡量提供：

- 問題的性質與影響範圍
- 重現步驟或概念性驗證
- 受影響的版本與環境
- 建議的修正方向（如有）

## 資料與隱私

- 本程式不收集、傳送或上傳任何個人資料或遙測。
- 設定與視窗狀態僅儲存於本機 `%AppData%\AndroidSync\settings.json`。
- 所有連線皆透過本機 ADB 或區域網路進行，不會連線至第三方伺服器。
- 詳細說明請見 [docs/PRIVACY.md](docs/PRIVACY.md)。

## 第三方元件

本專案內附 `scrcpy-server`（Apache License 2.0）；`adb` 需由使用者自行安裝，未隨本專案散布。請見 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
