# Agy CLI API Wrapper

A .NET 10 Web API and MudBlazor dashboard wrapper around Google Antigravity (`agy`) CLI, providing OpenAI-compatible endpoints (`/v1/chat/completions`, `/v1/models`), isolated worker pooling, streaming reasoning extraction, and SQLite-backed API key management.

---

## Features

- **OpenAI-Compatible REST API**:
  - `POST /v1/chat/completions` (SSE streaming and non-streaming responses, tool calls, usage accounting)
  - `GET /v1/models` (dynamically queries available models via `agy models`)
  - `GET /healthz` (readiness probe checking SQLite, `agy` CLI availability, and worker pool status) and `GET /health` (liveness probe)
- **Reasoning & Thought Streaming**:
  - Supports `reasoning_content` delta streaming and `<think>` tag formatting (`field`, `tags`, `both`)
  - Supports configurable `reasoning_effort` (`low`, `medium`, `high`, `xhigh`, `max`)
- **Isolated Worker Pooling**:
  - Bounded worker pool (`MaxPoolSize` + acquisition timeout), eager startup standby warm-up, session affinity, clean standby reuse, idle worker recycling, and strict agentic/sandbox worker segregation
  - Automatic retirement on client cancellation or unhealthy worker exit
- **MudBlazor Admin Dashboard**:
  - Interactive web UI at `/` for monitoring workers, testing inference, and managing API keys and token telemetry
- **Secure by Default**:
  - Binds `127.0.0.1:5200` by default with loopback-only + allowlisted CORS
  - Admin UI (`/`), Blazor circuit (`/_blazor`), and key management (`/api/keys`) require localhost access or an explicit `Agy:AdminApiKey`
  - Remote `/v1/*` calls require authentication (never fail open remotely)
  - API keys are stored as **SHA-256 hashes** (`key_hash`) in SQLite WAL mode (never plaintext at rest)
  - `AllowAgenticTools` defaults to `false`: agentic overrides (`agent:` prefix / `X-Agy-Mode: agent`) are rejected with `403`, and non-agentic workers run with `--sandbox` (without `--dangerously-skip-permissions`)
- **Docker Support**:
  - Non-root `Dockerfile` and `docker-compose.yml` configured to mount the host `agy` binary (`/usr/local/bin/agy`)

---

## Architecture & Vertical Slices

```
src/AgyApi/
├── Components/                 # MudBlazor interactive dashboard & layouts
├── Features/
│   ├── ApiKeys/                # SQLite SHA-256 key management & usage recording
│   ├── ChatCompletions/        # OpenAI chat endpoint, SSE streaming & parsers
│   ├── Health/                 # Readiness (/healthz) and liveness (/health) checks
│   ├── Models/                 # Native model discovery (`agy models`) & mapping
│   └── Sessions/               # Session mapping and prompt formatting
├── Infrastructure/
│   ├── Agy/                    # CLI execution, reverse transcript reader & worker pool
│   ├── Auth/                   # API key & admin authentication middleware
│   ├── Background/             # Warm-up and periodic maintenance service
│   └── Configuration/          # Strongly typed AgyOptions
tests/
└── AgyApi.Tests/               # xUnit unit & integration test suite
```

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [Google Antigravity CLI (`agy`)](https://antigravity.google) installed and authenticated in system `PATH`

### Running Locally

```bash
# Restore dependencies and build
dotnet build

# Run the API server (binds http://127.0.0.1:5200 by default)
dotnet run --project src/AgyApi
```

Access the MudBlazor Dashboard locally at `http://127.0.0.1:5200/`.

To expose admin UI or `/api/keys` beyond localhost, configure `Agy:AdminApiKey` (e.g. `Agy__AdminApiKey=<strong-secret>`) and pass it via `Authorization: Bearer <secret>` or `X-Admin-Key: <secret>`.

### Running with Docker

Ensure the host `agy` binary path is set (defaults to `/usr/local/bin/agy`, override with `AGY_BIN_PATH`):

```bash
AGY_BIN_PATH=$(which agy) AGY_ADMIN_API_KEY=<optional-admin-secret> docker compose up -d
```

---

## API Usage Example

```bash
curl http://127.0.0.1:5200/v1/chat/completions \
  -H "Authorization: Bearer <YOUR_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gemini-3.8-flash-medium",
    "messages": [{"role": "user", "content": "Explain quantum computing briefly"}],
    "stream": true
  }'
```

---

## Testing

Run the fast, deterministic unit test suite (offline/CI-safe):

```bash
dotnet test --filter "Category!=Integration"
```

Run live `agy` CLI integration tests (requires authenticated `agy` in `PATH`):

```bash
AGY_RUN_INTEGRATION_TESTS=1 dotnet test --filter "Category=Integration"
```

---

## License

MIT
