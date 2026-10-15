# Фотоматеріали демонстраційних дощок PH-08

Дата: 15.10.2026. Створено вбудованим інструментом `imagegen` (генерація нових зображень, без референсів). Це окремі фотографічні активи для реального інтерфейсу, а не зображення інтерфейсу замість Avalonia.

Спільні умови промптів: photorealistic natural board cover; cohesive calm editorial photography; landscape 3:2; central composition safe for a wide horizontal crop; realistic materials and natural light; no text, UI, logos or watermarks.

| Файл | Сцена промпту |
| --- | --- |
| `workspace.png` | Modern wooden desk with open laptop, coffee, plant and notebook beside a bright window. |
| `mountains.png` | Alpine turquoise lake with dramatic mountains and pine forest in clear daylight. |
| `library.png` | Cozy university library with books, study desk and green plant. |
| `team.png` | Four diverse adult university students collaborating around a laptop in a modern study space. |
| `planner.png` | Overhead blank weekly planner, pencil, coffee and laptop edge on a wooden desk. |
| `city.png` | Contemporary glass campus and office buildings with landscaping at golden hour. |

Усі PNG мають розмір 1536×1024 та менші за обмеження 5 МіБ. Переглянуті після генерації. Оригінали залишені в каталозі генератора; ці копії належать репозиторію. Демонстраційні дані мають завантажувати їх через звичайний захищений API зображень.

## Аватари вигаданих учасників

Також `imagegen` створив чотири окремі квадратні портрети 1254×1254 (кожен до 5 МіБ). Люди вигадані; фото не представляють реальних власників акаунтів.

Спільний промпт: Create one photorealistic square headshot avatar for a fictional Kanban coursework demo account. Head and shoulders, centered face with space around it for a circular crop, softly blurred neutral bright background, realistic unretouched skin texture, natural daylight, calm professional casual portrait, cohesive editorial photography. No text, logos, borders, UI or watermark.

- `avatar-anastasiia.png`: Ukrainian woman around 28, shoulder length brown hair, warm natural smile, light cream blouse.
- `avatar-olena.png`: Ukrainian woman around 27, dark bob haircut, natural smile, pale blue shirt.
- `avatar-dmytro.png`: Ukrainian man around 29, short dark brown hair and neat beard, navy shirt.
- `avatar-iryna.png`: Ukrainian woman around 26, light brown hair tied back, friendly smile, sage green sweater.

Портрети переглянуті після генерації. Скрипт `scripts/seed-demo.py` створює лише демонстраційні дані через локальний API; випадковий пароль та ID зберігаються у виключеному з Git `TestResults/ph08-demo.json`.
