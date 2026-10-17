# Іконка KanbanBoard

Власний геометричний знак: Kanban-дошка на бірюзовій плитці, колір `#1697A4`. Створено для цього проєкту 17.10.2026; сторонні зображення й шрифти не використані.

- `kanbanboard.svg` — редагований векторний оригінал 1024 × 1024.
- `kanbanboard.png` — растровий ресурс Avalonia 1024 × 1024 із прозорим полем.
- `kanbanboard.ico` — розміри 16, 32, 48, 64, 128 і 256 для executable та вікна.
- `kanbanboard.icns` — стандартний пакет macOS від 16 до 1024 пікселів.

PNG та ICO експортовані Magick.NET із SVG; ICNS зібрано штатним `iconutil` із PNG iconset. Скрипт `scripts/package-macos.sh` копіює ICNS у `Contents/Resources` і встановлює `CFBundleIconFile` у `Info.plist`.

Під час запуску клієнт також встановлює PNG як `NSApplication.applicationIconImage` у macOS. Це задає логотип у Dock і при запуску з Rider або `dotnet`, коли `Info.plist` пакета не використовується. Виклик виконується після ініціалізації Avalonia, лише на macOS; власні посилання NSData/NSImage звільняються після встановлення.
