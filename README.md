# OpenCode Local Provider Manager

Графическое приложение для Windows 10/11: поиск и управление **локальными LLM-провайдерами** в [OpenCode](https://opencode.ai).

![.NET](https://img.shields.io/badge/.NET-10.0-blue) ![Platform](https://img.shields.io/badge/platform-Windows-lightgrey) [![Release](https://img.shields.io/github/v/release/jeysson89/opencode-provider-manager)](../../releases)

## Возможности

- **Поиск локальных провайдеров** — сканирует известные порты и определяет запущенные сервисы:

  | Провайдер | Порт |
  |---|---|
  | Ollama | 11434 |
  | LM Studio | 1234 |
  | llama.cpp server | 8080 |
  | vLLM | 8000 |
  | Text Generation Inference | 8080 |
  | KoboldCpp | 5001 |
  | LocalAI | 7860 |

  Для найденных сервисов подтягивается актуальный **список моделей** (`/v1/models` или `/api/tags`).

- **Добавление в OpenCode** — провайдер записывается в конфиг с правильным форматом (`package`/`settings`), все найденные модели добавляются автоматически.
- **Редактирование провайдера** — название, baseURL, API-ключ, пакет.
- **Полный редактор моделей** — таблица «Ключ / modelID / Название»: добавление, правка, удаление; посторонние поля моделей (`limit` и др.) сохраняются.
- **Кнопка «Файл...» у API-ключа** — если ключ хранится ссылкой `{file:путь}`, редактирует сам файл секрета прямо из программы.
- **Изменения сохраняются на диск немедленно** — и в глобальный, и в локальный конфиги, каждый раз с резервной копией.

## Какие конфиги читаются/пишутся

| Файл | Секция | Формат |
|---|---|---|
| `~/.config/opencode/opencode.json` | `providers` | `settings` / `package` |
| `~/.local/share/opencode/opencode.json` | `provider` | `options` / `npm` |

Приложение поддерживает **оба формата** и синхронизирует провайдеров между ними.

## Установка

Скачайте `OpencodeProviderManager.exe` со страницы [Releases](../../releases/latest) и запустите.

> Требуется [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).

## Сборка из исходников

```bash
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=false -o publish
```

Результат: `publish/OpencodeProviderManager.exe` (~200 КБ, однофайловый).

## Структура

```
OpencodeProviderManager.csproj   # проект WinForms (.NET 10)
Program.cs                       # весь код: конфиги, сканер, UI
```

## Лицензия

MIT
