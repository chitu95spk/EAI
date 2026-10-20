# EAI-1 progress (root)
- Plan approved EAI-1-r1-plan-v1; auth: build + run_tests (NO train smoke). Decisions U1 dot-folder exclusion yes, U2/U4 accepted.
- S1 DONE 4e7a4bf10 (wrappers + model file, 13 SegAI tests). S2 DONE (data/options/raster/EV dataset, 29 SegAI tests). CmpType->CmpTypes root fix logged in issues.md.
- S3 DONE (converter, store, Core dot-folder skip; SegAI 39 + Core 42 tests). NEXT S4 inference+session, S5 training (no real run), S6 parity (ref48).
- S4 DONE (session + predictor, SegAI 49 tests). NEXT S5 training (no real run; auth lacks train), S6 parity ref48. Unverified: sub-ROI validMask guess in SegAiPredictor.
