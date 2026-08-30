---
id: 01m17z6g5fjtk619dz2kd04pqd
type: decision
status: active
tags:
- architecture
- project_scope
- user_home_isolation
- dev_first_workflow
- decision
created_at: 2026-08-29T23:55:37.0095101+00:00
updated_at: 2026-08-30T03:39:59.2488443+00:00
source:
---
User Home Isolation & Dev-First Workflow:

1. User Home root (~ / local user profile folder) is NOT a project repository; creating .eling directly in user home root is strictly forbidden.
2. ProjectScope.Discover excludes UserProfile home from project walk-up candidate resolution.
3. Eling.Host startup under user home CWD automatically maps data directory to ~/.config/eling (UserScope.GlobalDataDirectory).
4. RuntimeRegistry.Alive() filters out UserProfile and GlobalDataDirectory so dashboard only renders true project repositories.
5. Development Workflow Rule: ALWAYS verify and test thoroughly in Dev environment (port 4417/4427) before publishing globally to Staging!