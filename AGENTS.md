# Git-native persistent memory engine for AI coding agents

# Build backend
dotnet build Eling.slnx

# Run backend tests
dotnet test Eling.slnx

# Install frontend deps
pnpm --prefix src/frontend/Eling.Dashboard install

# Build frontend
pnpm --prefix src/frontend/Eling.Dashboard build
