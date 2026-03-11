# 📡 Basic gRPC Service in C#

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![gRPC](https://img.shields.io/badge/gRPC-AspNetCore-0097A7?style=flat-square&logo=google&logoColor=white)
![CloudEvents](https://img.shields.io/badge/CloudEvents-1.0-FF6B35?style=flat-square)
![buf](https://img.shields.io/badge/buf-v2-0078D4?style=flat-square)
![License](https://img.shields.io/badge/license-MIT-brightgreen?style=flat-square)

A minimal **ASP.NET Core (.NET 10)** gRPC service exploring three communication
patterns — unary, bidirectional streaming, and server-side streaming — with every
response wrapped in a [CloudEvents v1.0](https://cloudevents.io/) envelope.

Protobuf code is generated with the **buf CLI** rather than Grpc.Tools, keeping
generation explicit and reproducible. The server speaks **HTTPS only**, locally and
in production.

---

## ✨ Features

- 🔁 **Unary RPC** — `Hello` returns a personalised greeting as a Cloud Event
- 💬 **Bidirectional streaming** — `Talk` drives a real-time chat backed by an [ELIZA](https://en.wikipedia.org/wiki/ELIZA) chatbot
- ⚡ **Server streaming** — `Background` fans out across N concurrent fake service calls and streams cumulative results back as each one completes
- 🏥 **gRPC Health Checking Protocol** — available in all environments
- 🔍 **gRPC Server Reflection** — enabled in `Development` only
- 🔐 **HTTPS only** — Kestrel is configured with a single TLS endpoint; plain HTTP is never served

---

## 🗂️ Project Structure

```
basic-grpc-service-csharp/
├── 🔐 certs/                        # TLS certificates (git-ignored)
│   └── .gitkeep
├── ⚙️  gen/                          # Buf-generated C# — do not edit by hand
│   ├── Basic/
│   │   ├── Service/V1/              # Message types & enums
│   │   └── V1/                      # Service stub + reflection info
│   └── CloudEvents/V1/              # CloudEvent messages
├── 📜 proto/                         # Protobuf source of truth
│   ├── basic/
│   │   ├── service/v1/              # Messages, enums, Cloud Event payloads
│   │   └── v1/                      # BasicService RPC definition
│   └── io/cloudevents/v1/           # CloudEvents spec proto
├── 🛠️  Properties/
│   └── launchSettings.json          # dotnet run / IDE launch profile
├── 🧩 Services/
│   └── BasicServiceV1.cs            # gRPC service implementation
├── 🔧 Utils/
│   ├── Eliza.cs                     # ELIZA chatbot (DOCTOR script)
│   └── GeneratorUtils.cs            # Cloud Event factory & fake call helper
├── appsettings.json                 # Kestrel HTTPS endpoint (all environments)
├── appsettings.Development.json     # Local TLS certificate paths
├── buf.gen.yaml                     # Buf code generation config
├── buf.yaml                         # Buf module & lint config
└── Program.cs                       # Service registration & middleware pipeline
```

---

## 🧰 Prerequisites

| Tool | Version | Check |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0+ | `dotnet --version` |
| [buf CLI](https://buf.build/docs/installation) | v2+ | `buf --version` |
| [mkcert](https://github.com/FiloSottile/mkcert) | any | Recommended for local TLS |
| [grpcurl](https://github.com/fullstorydev/grpcurl) | any | Optional, for manual testing |

---

## 🚀 Getting Started

### 1️⃣ Clone the repository

```bash
git clone https://github.com/carvue/basic-grpc-service-csharp.git
cd basic-grpc-service-csharp
```

---

### 2️⃣ 🔐 Generate TLS certificates

The server expects a certificate at `certs/local.crt` and a private key at
`certs/local.key`. Both files are git-ignored — generate them once locally.

#### 🏆 Option A — mkcert (recommended)

mkcert installs a root CA into your system and browser trust stores, so you get
zero SSL warnings and no need for `-insecure` flags anywhere.

**Install mkcert**

<details>
<summary>🍎 macOS</summary>

```bash
brew install mkcert
brew install nss   # only needed if you use Firefox
```

</details>

<details>
<summary>🐧 Linux</summary>

```bash
# Debian / Ubuntu
sudo apt install libnss3-tools
curl -sSL https://github.com/FiloSottile/mkcert/releases/latest/download/mkcert-v*-linux-amd64 \
     -o /usr/local/bin/mkcert && chmod +x /usr/local/bin/mkcert

# Arch
sudo pacman -S mkcert

# Fedora / RHEL
sudo dnf install mkcert
```

</details>

<details>
<summary>🪟 Windows</summary>

```powershell
# winget
winget install FiloSottile.mkcert

# or Chocolatey
choco install mkcert

# or Scoop
scoop install mkcert
```

</details>

**Install the local CA and generate the certificate**

```bash
mkcert -install   # run once per machine
mkcert -cert-file certs/local.crt -key-file certs/local.key localhost 127.0.0.1 ::1
```

---

#### 🔧 Option B — OpenSSL (no extra tooling)

Generates a self-signed certificate. Clients will need to trust it manually or
connect with verification disabled (e.g. `grpcurl -insecure`).

<details>
<summary>🍎 macOS / 🐧 Linux</summary>

```bash
openssl req -x509 -newkey rsa:4096 -sha256 -days 365 -nodes \
  -keyout certs/local.key \
  -out   certs/local.crt \
  -subj  "/CN=localhost" \
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1,IP:::1"
```

</details>

<details>
<summary>🪟 Windows — OpenSSL (via winget / Chocolatey / Scoop)</summary>

```powershell
winget install ShiningLight.OpenSSL

openssl req -x509 -newkey rsa:4096 -sha256 -days 365 -nodes `
  -keyout certs/local.key `
  -out   certs/local.crt `
  -subj  "/CN=localhost" `
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1,IP:::1"
```

</details>

<details>
<summary>🪟 Windows — PowerShell (no OpenSSL)</summary>

```powershell
$cert = New-SelfSignedCertificate `
  -DnsName "localhost" `
  -CertStoreLocation "cert:\CurrentUser\My" `
  -NotAfter (Get-Date).AddYears(1)

Export-Certificate -Cert $cert -FilePath certs\local.crt -Type CERT
```

> ⚠️ This exports a DER-encoded certificate without a separate private key file.
> Converting it to PEM requires additional steps — OpenSSL is strongly recommended
> on Windows when mkcert is not an option.

</details>

---

### 3️⃣ ⚙️ Generate protobuf code

The `gen/` directory contains C# generated by the buf CLI. Re-run whenever you
change a `.proto` file.

**Install buf**

<details>
<summary>🍎 macOS / 🐧 Linux</summary>

```bash
brew install bufbuild/buf/buf
```

</details>

<details>
<summary>🪟 Windows</summary>

```powershell
winget install bufbuild.buf
```

</details>

<details>
<summary>📦 npm (cross-platform)</summary>

```bash
npm install -g @bufbuild/buf
```

</details>

**Run generation**

```bash
buf generate
```

This executes both plugins defined in `buf.gen.yaml`:

| Plugin | Version | Generates |
|---|---|---|
| `buf.build/protocolbuffers/csharp` | v34.0 | Message types (`gen/**/*.cs`) |
| `buf.build/grpc/csharp` | v1.78.1 | Service stubs (`gen/**/BasicGrpc.cs`) |

All files land in `gen/` under the base namespace `BasicGrpcService`.

**Other useful buf commands**

```bash
buf lint                                      # lint against the STANDARD ruleset
buf breaking --against '.git#branch=main'     # detect breaking proto changes
```

---

### 4️⃣ 🏗️ Build & run

```bash
dotnet restore   # restore NuGet packages
dotnet build     # compile
dotnet run       # start the server
```

The server starts at **`https://localhost:9443`** (HTTP/2 only).

> 💡 `dotnet run` automatically picks up `appsettings.Development.json` and the
> `Development` launch profile, so the local certificates and log levels are applied
> without any extra flags.

**IDE quick-start**

| IDE | Steps |
|---|---|
| 🪟 Visual Studio | Open `.csproj` → press **▶ Run** |
| 🖥️ VS Code | Install [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) → **Run and Debug** → select `basic-grpc-service-csharp` |
| 🖥️ Rider | Open `.csproj` → press **▶ Run** |

---

## 📡 API Reference

All responses are wrapped in a [CloudEvents v1.0](https://cloudevents.io/) envelope
(`io.cloudevents.v1.CloudEvent`) carrying a packed protobuf payload.

### `basic.v1.BasicService`

| Method | Pattern | Request | Response |
|---|---|---|---|
| `Hello` | Unary | `HelloRequest` | `HelloResponse` |
| `Talk` | Bidirectional streaming | `TalkRequest` | `TalkResponse` |
| `Background` | Server streaming | `BackgroundRequest` | `BackgroundResponse` |

---

#### 👋 `Hello` — Unary

Returns a personalised greeting inside a Cloud Event.

```bash
grpcurl -insecure \
  -d '{"message": "World"}' \
  localhost:9443 basic.v1.BasicService/Hello
```

---

#### 🧠 `Talk` — Bidirectional Streaming

Real-time chat powered by the ELIZA chatbot (DOCTOR script). Every message sent
gets an immediate psychotherapist-style reply.

```bash
grpcurl -insecure \
  -d @ \
  localhost:9443 basic.v1.BasicService/Talk <<EOF
{"message": "I feel anxious"}
{"message": "My mother never listened to me"}
{"message": "goodbye"}
EOF
```

---

#### ⚡ `Background` — Server Streaming

Spawns `processes` concurrent fake service calls and streams a cumulative status
event as each one finishes — results arrive in **completion order**, not submission
order.

```bash
grpcurl -insecure \
  -d '{"processes": 5}' \
  localhost:9443 basic.v1.BasicService/Background
```

Each intermediate event carries state `PROCESS` with all responses collected so far.
The final event carries `COMPLETE` or `COMPLETE_WITH_ERROR`.

---

## 🏥 Health Checks & 🔍 Reflection

### Health checks

The [gRPC Health Checking Protocol](https://github.com/grpc/grpc/blob/master/doc/health-checking.md)
runs in **all environments** — ready for Kubernetes probes and load balancers out of
the box.

```bash
# Overall server health
grpcurl -insecure localhost:9443 grpc.health.v1.Health/Check

# BasicService-specific health
grpcurl -insecure \
  -d '{"service": "basic.v1.BasicService"}' \
  localhost:9443 grpc.health.v1.Health/Check
```

### Reflection

Server reflection is available in `Development` only, so your full schema is never
accidentally exposed in production.

```bash
grpcurl -insecure localhost:9443 list                           # all services
grpcurl -insecure localhost:9443 describe basic.v1.BasicService # service detail
```

---

## 🐳 Production Deployment

The server binds **HTTPS only**. Supply the production certificate via environment
variables — the standard .NET `__` separator maps directly to the config hierarchy,
no code changes needed.

```bash
Kestrel__Certificates__Default__Path=/run/secrets/service.crt
Kestrel__Certificates__Default__KeyPath=/run/secrets/service.key
```

**Docker**

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY publish/ .
ENTRYPOINT ["dotnet", "basic-grpc-service-csharp.dll"]
```

```bash
docker run \
  -e Kestrel__Certificates__Default__Path=/certs/service.crt \
  -e Kestrel__Certificates__Default__KeyPath=/certs/service.key \
  -v /host/certs:/certs:ro \
  -p 9443:9443 \
  basic-grpc-service-csharp
```

**☸️ Kubernetes liveness / readiness probes**

```yaml
livenessProbe:
  grpc:
    port: 9443
    service: ""
readinessProbe:
  grpc:
    port: 9443
    service: "basic.v1.BasicService"
```

---

## 📄 License

[MIT](LICENSE) © Carsten Vuellings
