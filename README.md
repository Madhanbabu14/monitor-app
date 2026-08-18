# Pipeline Control Center

Enterprise-grade monitoring platform for Azure Data Factory pipelines.

## Architecture

```
enterprise-grade application/
├── frontend/          # React 19 + Vite + TypeScript + MUI
├── backend/           # Node.js + Express + TypeScript
├── database/          # PostgreSQL schema + seeds
└── docker-compose.yml
```

## Quick Start

### Prerequisites
- Docker & Docker Compose
- Node.js 20+
- PostgreSQL 16+

### 1. Configure Environment

```bash
# Backend
cp backend/.env.example backend/.env
# Edit backend/.env with your Azure and AWS credentials

# Frontend
cp frontend/.env.example frontend/.env
```

### 2. Docker (Recommended)

```bash
docker-compose up -d
```

- Frontend: http://localhost:3000
- Backend API: http://localhost:4000
- PostgreSQL: localhost:5432

### 3. Local Development

**Backend:**
```bash
cd backend
npm install
npm run dev
```

**Frontend:**
```bash
cd frontend
npm install
npm run dev
```

## Default Users (dev seed)

| Email | Password | Role |
|-------|----------|------|
| admin@company.com | Admin@123 | Admin |
| operator@company.com | Admin@123 | Operator |
| viewer@company.com | Admin@123 | Viewer |

## Features

| Module | Description |
|--------|-------------|
| Dashboard | Live KPIs + pipeline run table + charts, auto-refresh 30s |
| Pipeline Details | Run info, activities, rerun controls |
| Error Analysis | Searchable error log with copy/export |
| Data Recovery | Backfill trigger with progress tracking |
| DB Validation | Table record counts, freshness checks |
| Reports | Daily processing charts + missing file detection |
| Configuration | Pipeline → DB table mapping management |
| Audit Logs | Full user action audit trail |

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | /api/pipelines/summary | Dashboard KPIs |
| GET | /api/pipelines | All active pipelines |
| GET | /api/pipelines/runs | Pipeline runs (paginated) |
| GET | /api/pipelines/runs/:id | Run details + activities |
| POST | /api/pipelines/runs/trigger | Trigger ADF pipeline |
| POST | /api/pipelines/runs/rerun-date-range | Date-range rerun |
| GET | /api/errors | Pipeline errors (paginated) |
| GET | /api/errors/export | Export error log |
| GET | /api/validation | Latest validation results |
| POST | /api/validation/run | Run validation |
| GET | /api/recovery | Recovery jobs |
| POST | /api/recovery | Create recovery job |
| GET | /api/reports/daily | Daily processing report |
| GET | /api/reports/missing-files | Missing file detection |
| GET | /api/configuration | Pipeline mappings |
| PUT | /api/configuration/:id | Update mapping (Admin) |
| POST | /api/configuration | Create mapping (Admin) |
| GET | /api/audit | Audit logs (Admin) |

## Tech Stack

**Frontend:** React 19, Vite, TypeScript, Material UI 5, React Router 6, TanStack Query 5, Recharts, Axios, Zustand, XLSX

**Backend:** Node.js 20, Express 4, TypeScript 5, pg (node-postgres), MSAL/JWKS-RSA, Winston, Express Rate Limit

**Database:** PostgreSQL 16

**Integrations:** Azure Data Factory REST API, Azure Monitor, AWS S3 SDK v3, Azure Identity SDK

## Environment Variables

### Backend (.env)

```
NODE_ENV=development
PORT=4000
DATABASE_URL=postgresql://user:pass@localhost:5432/pipeline_control_center
AZURE_TENANT_ID=
AZURE_CLIENT_ID=
AZURE_CLIENT_SECRET=
AZURE_SUBSCRIPTION_ID=
AZURE_RESOURCE_GROUP=
AZURE_DATA_FACTORY_NAME=
AWS_ACCESS_KEY_ID=
AWS_SECRET_ACCESS_KEY=
AWS_REGION=us-east-1
AWS_S3_BUCKET=
JWT_SECRET=
CORS_ORIGIN=http://localhost:3000
```

### Frontend (.env)

```
VITE_API_URL=http://localhost:4000/api
VITE_AZURE_CLIENT_ID=
VITE_AZURE_TENANT_ID=
```
