# AGENTS.md — Pemail

Инструкции для AI-агентов, работающих с кодовой базой **Pemail**.

## Стек и жёсткие ограничения

- **Платформа:** чистый `.NET Framework 4.8` + `WPF`, C# 5/6-совместимый синтаксис (компилируется системным `MSBuild.exe` из `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe`).
- **Ноль внешних зависимостей:** не добавлять NuGet-пакеты и сторонние DLL. Используются только системные сборки (`System.Security`, `System.Web.Extensions`, `PresentationFramework` и др.).
- **Язык C#:** не использовать фичи C# 7+ (кортежи, pattern matching `is var`, switch expressions, `using var`, интерполяцию `$""` в некоторых сборках старого компилятора лучше тоже избегать — проект собирается системным `csc.exe` 4.8).
- **Платформа сборки:** `PlatformTarget = x64`, `ApplicationIcon = app.ico`.

## Команда сборки

Из корня `Pemail` (в `sh` передавать ключи с `-`, а не `/`):

```sh
"/c/Windows/Microsoft.NET/Framework64/v4.0.30319/MSBuild.exe" Pemail.csproj -p:Configuration=Release -nologo -v:minimal
```

> Если `bin/Release/Pemail.exe` заблокирован (MSB3021/MSB3027), значит приложение запущено у пользователя — не завершать процесс принудительно, попросить пользователя закрыть окно.

## Архитектура и инварианты

1. **Безопасность данных пользователя:**
   - Пароли приложений шифруются через `ProtectedData` (`DataProtectionScope.CurrentUser`) в `Models/AccountConfig.cs`.
   - Никогда не коммитить `PemailData/`, `bin/`, `obj/`, `*.eml`, `*.eml.gz`, `accounts.json`, `index.json`, `contacts.json`.
   - Перед удалением писем с сервера в режиме архивации (`CleanupWindow`, `BtnArchiveSingle_Click`) **обязательно** проверять `ArchiveStore.VerifyLocalCopy(entry)`.

2. **Логика синхронизации и архива (`Storage/ArchiveStore.cs`, `UI/MainWindow.xaml.cs`):**
   - `OnServer = true, IsArchived = false` — обычное синхронизированное письмо. Если его удалили на сервере (Яндекс/Gmail), `ReconcileFolderServerUids` удаляет его и локально при синхронизации.
   - `IsArchived = true` — письмо явно отправлено в локальный архив (кнопка «В архив» или «Очистка ГБ»). Синхронизация никогда не удаляет архивные письма с диска, а лишь снимает флаг `OnServer = false`.
   - `RebuildFolderTree()` обязан сохранять текущую выбранную папку пользователя и не сбрасывать выделение на «Все письма».

3. **Сетевые протоколы (`Net/ImapClient.cs`, `Net/SmtpClientEx.cs`):**
   - Скачивание по умолчанию идёт через `BODY.PEEK[]`, чтобы не сбивать статус «непрочитано» на сервере (если не включено `MarkReadOnServer`).
   - Для реального освобождения квоты на Gmail и Яндексе удаление выполняется через `UID COPY` в Корзину (`\Trash`) + `+FLAGS.SILENT (\Deleted)` + `EXPUNGE` (и опционально `EmptyTrash`).
   - В `ImapClient.ListFolders()` папка `[Gmail]/All Mail` (`\All`) исключается из скачивания в `MainWindow` во избежание дублирования писем.
   - При отправке через Gmail `IMAP APPEND` в `Sent` не делается (Gmail сам сохраняет отправленные письма в `Sent`).

4. **UI и темы (`App.xaml`, `UI/ThemeManager.cs`):**
   - Все цвета интерфейса должны ссылаться на `{DynamicResource ...}`, чтобы переключение светлой и мягкой тёмной темы работало мгновенно во всех окнах.
   - Для `TreeViewItem` не задавать жёсткие цвета выделения в `Style.Resources` — они управляются через `SystemColors.HighlightBrushKey` / `InactiveSelectionHighlightBrushKey` в `ThemeManager`.
   - Все иконки в интерфейсе — векторные `Geometry` (`Path`) в `App.xaml`, без юникод-эмодзи.
   - В списке писем используется `SelectionMode="Extended"` (`Shift`/`Ctrl`), без чекбоксов.
   - В `ComposeWindow` поддерживается отправка по `Ctrl+Enter` и автоподсказки адресов из `ArchiveStore.SuggestContacts`.
