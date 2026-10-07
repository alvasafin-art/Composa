# Обмен изображениями с Photoshop

Обмен с уже работающими Composa и Photoshop возможен без узлов ComfyUI и без изменения редактора. В `scripts/photoshop` подготовлен экспериментальный мост для Windows: PowerShell обращается к существующему MCP Composa, Photoshop принимает изображение через COM, а ExtendScript отправляет копию текущего изображения обратно. Скрипты не устанавливаются автоматически.

## Из Composa в Photoshop

Откройте обе программы. Запустите `scripts/photoshop/Send-to-Photoshop.cmd`, либо выполните команду в **Windows PowerShell 5.1**:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "scripts/photoshop/Exchange.ps1" -Direction ToPhotoshop -ComposaExe "полный путь к composa.exe"
```

Composa экспортирует текущий документ в новый временный PNG. Photoshop открывает его новым документом. Исходный проект и его путь сохранения не меняются. Скрипт требует зарегистрированного COM-интерфейса Photoshop и отказывается работать, если Photoshop не запущен.

## Из Photoshop в Composa

Задайте переменную окружения `COMPOSA_EXE` с полным путём к `composa.exe` до запуска Photoshop, либо используйте скрипты из этого репозитория рядом с готовой сборкой в `dist`. Оставьте `Exchange.ps1` и `Send-to-Composa.jsx` в одной папке. В Photoshop выберите **File → Scripts → Browse…** и откройте `Send-to-Composa.jsx`. Его также можно запускать действием Photoshop и назначить этому действию клавишу.

Скрипт экспортирует дубликат активного документа в PNG и открывает его новой вкладкой уже работающей Composa через `open_document`. Исходный Photoshop-документ не редактируется. Временные PNG сохраняются в системной временной папке; после закрытия переданных документов их можно удалить.

## Ограничения и дальнейшее развитие

Это передача видимого изображения: слои, выделения и редактируемый текст не переносятся. Копия из Photoshop переводится в RGB / 8 бит. Цветовой профиль может влиять на вид при открытии в другом приложении. Для сохранения слоёв уже существует обмен PSD; Composa сообщает о преобразованиях неподдерживаемых функций.

Мост проверен в Windows PowerShell 5.1: прошли синтаксическая проверка, инициализация настоящего MCP-моста из готовой сборки и обработка отсутствующего редактора. Реальный двусторонний обмен требует проверки с запущенным Photoshop; он не выполнялся автоматически в рамках исследования.

Для удобной постоянной панели и поддержки macOS лучше отдельный UXP-плагин Photoshop с разрешённой папкой обмена и локальным транспортом. UXP предоставляет открытие/сохранение документов и `batchPlay`; операции, меняющие состояние Photoshop, выполняются через `executeAsModal`. Источники: [Adobe Photoshop API](https://developer.adobe.com/photoshop/uxp/ps_reference/), [executeAsModal](https://developer.adobe.com/photoshop/uxp/2022/ps-reference/media/executeasmodal), [автоматизация скриптами](https://helpx.adobe.com/photoshop/desktop/automate-tasks/automation-settings-and-presets/automate-photoshop-with-scripts.html).
