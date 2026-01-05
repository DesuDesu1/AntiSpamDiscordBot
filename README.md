# AntiSpam Discord Bot

## Структура

```
src/
├── AntiSpam.Contracts/    # DTOs, Events для Kafka
├── AntiSpam.Gateway/      # Discord WebSocket + Kafka Producer
└── AntiSpam.Bot/          # Kafka Consumers + бизнес-логика

deploy/
└── helm/antispam/         # Helm chart для K8s
```

## Локальная разработка

```bash
# Build
dotnet build

# Run Gateway
dotnet run --project src/AntiSpam.Gateway

# Run Bot
dotnet run --project src/AntiSpam.Bot
```

## Деплой

### Prerequisites на VPS

1. K3s:
```bash
curl -sfL https://get.k3s.io | sh -
```

2. Helm:
```bash
curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash
```

3. Клонируй репо:
```bash
git clone <repo> /opt/antispam
```

### GitHub Secrets

Настрой в репозитории Settings → Secrets:

- `VPS_HOST` — IP адрес сервера
- `VPS_USER` — пользователь SSH
- `VPS_SSH_KEY` — приватный SSH ключ
- `DISCORD_TOKEN` — токен Discord бота
- `POSTGRES_PASSWORD` — пароль PostgreSQL

### Ручной деплой

```bash
helm upgrade --install antispam ./deploy/helm/antispam \
  --set discord.token=YOUR_TOKEN \
  --set postgresql.password=YOUR_PASSWORD \
  --set gateway.image.repository=ghcr.io/YOUR_USERNAME/antispam-gateway \
  --set bot.image.repository=ghcr.io/YOUR_USERNAME/antispam-bot \
  --namespace antispam \
  --create-namespace
```

## Мониторинг

```bash
# Статус подов
kubectl get pods -n antispam

# Логи Gateway
kubectl logs -f deployment/antispam-gateway -n antispam

# Логи Bot
kubectl logs -f deployment/antispam-bot -n antispam
```
