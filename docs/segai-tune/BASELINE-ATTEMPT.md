# Native baseline train attempt

2026-11-10: native ConfigureV2 and all 150 AddSample calls succeeded. TrainerTrain returned -8: `V2 load backbone backend=Intel GPU input=1280x1024: Intel OpenVINO GPU unavailable. Select CPU or check the Intel driver.` No baseline model saved by this failed attempt.

Required runtime adaptation: `gpu=0` (CPU). Other baseline defaults unchanged: Side640, epochs200, lr0.015, regularization0.001, rotation15, scaleVariation0.15, brightness0.15, augmentation on, early stopping on. Input mode remains fixed Side640, originalInput0, inference speed scale1.0.
