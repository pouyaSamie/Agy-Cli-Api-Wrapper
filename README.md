# Agy CLI API Wrapper

A high-performance .NET 10 Web API and MudBlazor dashboard wrapper around Google Antigravity (`agy`) CLI, providing OpenAI-compatible endpoints (`/v1/chat/completions`, `/v1/models`), worker pooling, streaming thoughts & reasoning, and SQLite-backed API key management.

---

## Features

- **OpenAI-Compatible REST API**:
  - `POST /v1/chat/completions` (supports both SSE streaming and standard responses)
  - `GET /v1/models` (dynamically queries available models from `agy --help`)
  - `GET /healthz` (service health and readiness probe)
- **Deep Reasoning & Thought Streaming**:
  - Supports `reasoning_content` delta streaming and `<think>` encapsulation
  - Supports configurable `reasoning_effort` (`low`, `medium`, `high`)
- **Process & Worker Pooling**:
  - Intelligent multi-worker pool with auto-scaling, warm pre-spawning, idle worker recycling, and per-worker turn limits
  - Resilient child-process lifecycle management
- **MudBlazor Admin Dashboard**:
  - Interactive web UI at `/` for monitoring active workers, pool health, and metrics
  - API key generation, revocation, and per-key token & request usage tracking
- **Secure by Default**:
  - Bearer token authentication via `ApiKeyAuthMiddleware`
  - Encrypted storage using SQLite with WAL mode
- **Container Ready**:
  - Pre-configured `Dockerfile` and `docker-compose.yml`

---

## Architecture & Vertical Slices

The project follows clean architecture principles and vertical slice decomposition:

```
src/AgyApi/
├── Components/                 # MudBlazor interactive dashboard & layouts
├── Configuration/              # Strongly typed AgyOptions
├── Features/
│   ├── ApiKeys/                # SQLite key management & usage recording
│   ├── ChatCompletions/        # OpenAI chat endpoint & streaming logic
│   ├── Health/                 # Health checks
│   ├── Models/                 # Native model discovery & mapping
│   └── Sessions/               # Session mapping and conversation isolation
├── Infrastructure/
│   ├── Agy/                    # CLI execution, transcript reading & worker pool
│   ├── Auth/                   # API key authentication middleware
│   └── Background/             # Maintenance and background tasks
tests/
└── AgyApi.Tests/               # Comprehensive xUnit & Moq unit test suite
```

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [Google Antigravity CLI (`agy`)](https://antigravity.google) in system PATH

### Running Locally

```bash
# Restore dependencies and build
dotnet build

# Run the API server
dotnet run --project src/AgyApi
```

Access the MudBlazor Dashboard at `http://localhost:5200/`.

### Running with Docker

```bash
docker compose up -d
```

---

## API Usage Example

```bash
curl http://localhost:5200/v1/chat/completions \
  -H "Authorization: Bearer <YOUR_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gemini-3.8-flash-high",
    "messages": [{"role": "user", "content": "Explain quantum computing briefly"}],
    "stream": true
  }'
```

---

## Testing

Run unit tests across the test suite:

```bash
dotnet test
```

---

## License

MIT
