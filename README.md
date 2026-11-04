# Адаптивная синхронизация локальной и серверной БД с учётом версий схем

Экспериментальный прототип к статье «Адаптивный метод синхронизации локальной и серверной баз данных мобильной
информационной системы с учётом версионной совместимости схем данных».

## Состав

| Что | Где |
| --- | --- |
| Клиент синхронизации (SQLite) | `adaptive-sync/src/Sync.Client` |
| Сервер синхронизации (ASP.NET Core, PostgreSQL) | `adaptive-sync/src/Sync.Server` |
| Сервис разрешения конфликтов | `adaptive-sync/src/Sync.Resolver` |
| Адаптивный метод и базовые стратегии | `adaptive-sync/src/Sync.Strategies` |
| Реестр схем v1–v3, протокол | `adaptive-sync/src/Sync.Core` |
| Оракул (эталон критериев S1–S5) | `adaptive-sync/src/Sync.Oracle` |
| Генератор сценариев, метрики, статистика | `adaptive-sync/src/Sync.Simulation` |
| Запуск экспериментов | `adaptive-sync/src/Sync.Bench` |
| Тесты | `adaptive-sync/tests` |
| Демо-приложение Unity | `Assets/AdaptiveSync`, сцены `Assets/Scenes/Boot.unity`, `Main.unity` |
| Результаты экспериментов | `docs/results` |
| Описание метода, метрик и методики | `docs/method.md` |
| Сценарий демонстрационного видео | `docs/demo.md` |

## Результаты

- `docs/results/summary.md`, `results.csv` — сравнение стратегий с оракулом (59 сценариев, 3640 событий);
- `docs/results/statistics.md`, `statistics.csv` — 30 seed на точку, 95 % доверительные интервалы, ablation;
- `docs/results/charts/*.svg` — графики по сериям;
- `docs/results/end-to-end.md`, `end-to-end.csv` — время и объём данных на реальном стенде.

## Воспроизведение

Нужны .NET 8 SDK и Docker. Из папки `adaptive-sync`:

```
dotnet test AdaptiveSync.sln
dotnet run --project src/Sync.Bench -c Release -- ../docs/results 5 30 summary
dotnet run --project src/Sync.Bench -c Release -- ../docs/results 5 30 stats
docker compose up -d --build --wait
dotnet build AdaptiveSync.sln -c Release
dotnet run --project src/Sync.Bench -c Release --no-build -- ../docs/results 5 30 e2e 5
```

Генератор детерминирован: тот же seed даёт тот же журнал, поэтому таблицы и графики совпадают при повторном запуске
(кроме замеров времени).
