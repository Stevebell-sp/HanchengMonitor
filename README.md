# 瀚誠系統監控（HanchengMonitor）

Windows 桌面系統監控小工具，毛玻璃半透明外觀，橫式或直式長條顯示電腦即時狀態。

![監控條](docs/screenshot-bar.png)

## 功能

- **監控項目**：CPU 使用率／溫度／時脈／功耗、最忙程式、GPU 使用率／溫度／顯示記憶體／時脈／功耗／風扇、記憶體、各磁碟讀寫、硬碟繁忙、網路上傳下載、風扇轉速、電池、開機時間，以及「變慢原因」
- **外觀**：Windows 11 毛玻璃（Acrylic）、深色／淺色、華麗（發光晶片）／簡約兩種風格、5 組配色與單項自訂顏色、可調底色濃度與大小
- **點格子看詳細**：最近約 5 分鐘走勢圖、各核心使用率、前 10 名程式、所有溫度與風扇感測器
- **遊戲模式**：偵測到遊戲（預設 `javaw`，也可把全螢幕程式算進去）時自動置頂，可自訂遊戲中顯示的項目、排列與位置；與「永遠置頂」各自獨立
- **過熱與滿載警示**：超過門檻時格子閃爍並跳出 Windows 通知
- **歷史紀錄**：每分鐘記錄溫度與使用率，可回顧任一天的走勢
- **其他**：不會被 Win+D 收起、滑鼠穿透（按住 Alt 可暫時點選）、鎖定位置、吸附螢幕邊緣、系統匣選單、開機自動啟動、從 GitHub Releases 自動更新

## 安裝

到 [Releases](https://github.com/Stevebell-sp/HanchengMonitor/releases) 下載 `HanchengMonitor_Setup_x.y.z.exe` 執行即可。安裝檔內含 .NET 執行環境，不需另外安裝。

- 程式需要系統管理員權限（讀取 CPU 溫度用）。
- 讀取 CPU 溫度需要 [PawnIO](https://github.com/namazso/PawnIO) 驅動程式，安裝時可勾選一併安裝。

## 自行編譯

需要 .NET 8 SDK 與 Inno Setup 6.5 以上。

```powershell
dotnet build -c Release
```

打包並發佈新版本（需要已登入的 GitHub CLI）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Notes "更新內容"
```

加上 `-NoUpload` 只打包、不上傳。

## 使用的第三方元件

- [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)（MPL-2.0）
- [PawnIO](https://github.com/namazso/PawnIO.Setup)（安裝程式為 GPL-2.0，隨附未修改的官方安裝檔）
- .NET Runtime（MIT）
