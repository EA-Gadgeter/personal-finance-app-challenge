# Run the signup milestone

Install the .NET 10 SDK, Node.js with npm, Docker Compose, and the EF Core CLI 10.x. Run these commands from the repository root.

## Database and API

Choose a PostgreSQL password before creating the Docker volume. Reuse that password when starting the same volume later. A hexadecimal password can be pasted directly into the connection string.

```bash
read -rsp 'Local PostgreSQL password: ' FINANCE_DB_PASSWORD
printf '\n'
export FINANCE_DB_PASSWORD
docker compose up -d --wait postgres

dotnet user-secrets --project api/Finance.Api.csproj set ConnectionStrings:Finance \
  "Host=127.0.0.1;Database=finance;Username=finance;Password=$FINANCE_DB_PASSWORD"
dotnet user-secrets --project api/Finance.Api.csproj set Auth:SigningKey "$(openssl rand -base64 32)"

export ASPNETCORE_ENVIRONMENT=Development
dotnet ef database update --project api/Finance.Api.csproj
dotnet run --project api/Finance.Api.csproj --no-launch-profile --urls http://127.0.0.1:5075
```

Set the signing key once. Changing it invalidates existing JWTs.

## React

In a second terminal, export a local HTTPS certificate and start Vite.

```bash
mkdir -p web/.certs
dotnet dev-certs https --trust --export-path web/.certs/localhost.pem --format PEM --no-password
npm --prefix web install
npm --prefix web run dev
```

Open [https://localhost:5173](https://localhost:5173). Vite forwards `/api` to the local ASP.NET process. After creating an account, sign in with the same credentials. The account starts empty.

## Tests

Use a separate PostgreSQL database for the integration tests. Create the database first and set `FINANCE_AUTH_TEST_CONNECTION` to its connection string. The tests create unique accounts in that database and use an injected clock to test session expiry.

```bash
dotnet ef database update --project api/Finance.Api.csproj --connection "$FINANCE_AUTH_TEST_CONNECTION"
dotnet test tests/Finance.Api.Tests.csproj
npm --prefix web run build
```

Run the request validation and JWT rejection tests without PostgreSQL:

```bash
dotnet test tests/Finance.Api.Tests.csproj --filter 'Dependency!=PostgreSQL'
```

Run the frontend unit tests with `npm --prefix web test`. They use Node's built-in test runner.

## Branches and pull requests

- `main` holds the release history. Promote `develop` into it through a PR when a release is ready.
- `develop` is the integration branch. Create feature branches from its latest commit.
- Name feature branches `feature/<short-name>`. Open their PRs against `develop`.

The **Pull request** workflow runs **API tests** and **Frontend tests and build** on PRs into both branches. In GitHub, protect `develop` and `main` by requiring pull requests and both checks from GitHub Actions. Select the check names after the workflow has reported them on a PR. Enable **Require branches to be up to date before merging** to validate against the current base, and disallow bypasses if every merge must pass.
