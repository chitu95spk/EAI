# EAI-LABEL plan v1 (architect output, digest) - NOT yet approved by the user

Goal: bootstrap labelling of E:\EAI_TEST (84 PNG: 51 NG, 33 OK, 2448x2048, no labels): agent labels k seed images -> small Segment AI model -> model labels the rest -> only uncertain/failed images go back to the agent. Targets: 4x faster, 50% cheaper than today's Agent Prompt batch (to be measured).

Facts: agent batch is sequential, one CLI process per image, only a text 'experience' block is reused (AgentBatch.cs:410-419). Local-first routing already exists (AgentRouteLocalFirst + AgentBatchImage.LocalUncertainty, AgentBatch.cs:273-342). Missing: seed, split, train, activate (SetActiveModel), auto-label, accept loop. EasyLabel is net9, LangVersion 12 (Directory.Build.props), so the C# 7.3 rule does not apply here.

State machine: seed(k agent images, prefer NG) -> user confirms seeds -> AutoAssignVal -> train -> gate (Val + p=2 probe images the agent also labels; grow k 2->3->5, max 8; else fall back to plain agent batch) -> activate -> AutoLabelService on the rest -> route Doubtful/Unsure/zero-detect/error to the agent -> accept Sure results (manifest rel->source for undo).
k: min 2, recommend 3 (+p=2 probe), measure 2/3/5.

Model (estimates): W1=(k+p)a+T+(N-k)m+f(N-k)a ; C1=(k+p+f(N-k))c. Cost target holds for fallback fraction f<=~0.46 (k=3,p=2). Speed target needs f<=~0.17 (a=90 s, T=60 s, m=2 s). 33 OK images (39%) make 4x unreachable if zero-detection images always go to the agent.

Slices: S0 baseline (no product change; real Agent Prompt on all 84, Claude sonnet defaults, caps raised in Settings only, log.csv window) -> S1 offline replay test (BootstrapReplayTests, env EASYAI_RUN_BOOTSTRAP=1; T, m, f per k; V1 vs V2 decision) -> S2 BootstrapLabelPlan/Runner (Services/Agent, unit tests on fakes) -> S3 LabelStepViewModel.Bootstrap.cs + settings + en/vi strings -> S4 two real runs + UI smoke 100%/150% DPI. Commit per slice, prefix [EAI-LABEL], no push.

Quality (vs user's ~10 ground-truth images, not used as seeds): IoU>=0.5, recall>=0.85, precision>=0.70, no false NG on GT-OK, shape-count error<=1, recall >= baseline-0.05. Claim 4x/-50% only if two runs both meet it.

Risks: weak model zero detections hide defects (ImageUncertainty.cs:114-123 scores empty = Sure); V1 needs NG and OK labels, named classes need V2 + backbone; 2-core PC training time unmeasured; Claude non-determinism; real runs cost money.
