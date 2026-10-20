import os
os.chdir(r'E:\Code\EasyVision_Unisen - NoService\BeeNativeSegAI')
p = 'SegV2Core.cpp'
s = open(p, encoding='utf8').read()
orig = s


def rep(old, new, count=1):
    global s
    assert s.count(old) == count, (s.count(old), old[:90])
    s = s.replace(old, new)


# 1) the combination is allowed now
rep('        if (p.backboneEpochs>0) throw std::runtime_error("Instance head (instanceHead=1) cannot be combined with the backbone fine-tune (backboneEpochs>0)");\n',
    '        // EAI-LOGO: instanceHead + backboneEpochs is allowed. The instance rows are then gathered in a second pass, from the features of\n'
    '        // the fine-tuned backbone (see deferInstance in Train), so the instance head always learns from the backbone it will run on.\n')
rep('// Rejected up front (r1 scope): original-input models, tiled input, backbone fine-tune, Retrain / resume / changed-only training.',
    '// Rejected up front (r1 scope): original-input models, tiled input, Retrain / resume / changed-only training.')

# 2) runJob: instanceOnly parameter
rep('                        InstanceTrainRows* instanceOut)->Impl::Sample {',
    '                        InstanceTrainRows* instanceOut,bool instanceOnly)->Impl::Sample {')
rep('            Impl::Sample sampled=p.Gather(featureRows,frame,random);',
    '            Impl::Sample sampled;\n            if(!instanceOnly) sampled=p.Gather(featureRows,frame,random);')

# 3) deferInstance flag next to the instance bookkeeping
rep('        // EAI-POS P3: per-job instance rows (non-held jobs only), in job order; counters for the status file.',
    '        // EAI-LOGO: with a backbone fine-tune the instance rows wait for the fine-tuned features (second pass below).\n'
    '        const bool deferInstance=p.instanceHead&&p.backboneEpochs>0;\n'
    '        // EAI-POS P3: per-job instance rows (non-held jobs only), in job order; counters for the status file.')
rep('results[index]=runJob(jobs[index],requests,tiledCompiled,tiledRequest,tiledSize,p.instanceHead&&!jobs[index].held?&instanceResults[index]:nullptr);++done;',
    'results[index]=runJob(jobs[index],requests,tiledCompiled,tiledRequest,tiledSize,p.instanceHead&&!deferInstance&&!jobs[index].held?&instanceResults[index]:nullptr,false);++done;')

# 4) second pass after the semantic head is trained
rep('        // EAI-POS P3: the instance branch is trained after (and independently of) the semantic head: own rows, RNG, Adam. A failure',
    '        if(deferInstance) {\n'
    '            // EAI-LOGO: fine-tune the backbone first, then gather the instance rows again from the fine-tuned features.\n'
    '            { Impl::StageTimer timer(p,"finetune_backbone");p.stage="fine-tune backbone";p.FineTuneBackbone(features,head); }\n'
    '            compiledBySize.clear();\n'
    '            p.stage="sample instance features";\n'
    '            std::map<std::pair<int,int>,ov::InferRequest> secondRequests;ov::CompiledModel secondTiledCompiled;ov::InferRequest secondTiledRequest;cv::Size secondTiledSize;\n'
    '            Impl::StageTimer secondTimer(p,"instance_features_second_pass");\n'
    '            for(size_t index=0;index<jobs.size();++index) {\n'
    '                if(jobs[index].held) continue;\n'
    '                p.CheckCancel();\n'
    '                runJob(jobs[index],secondRequests,secondTiledCompiled,secondTiledRequest,secondTiledSize,&instanceResults[index],true);\n'
    '            }\n'
    '        }\n'
    '        // EAI-POS P3: the instance branch is trained after (and independently of) the semantic head: own rows, RNG, Adam. A failure')

# 5) the old fine-tune call only when it did not run above
rep('        if(p.backboneEpochs>0) { Impl::StageTimer timer(p,"finetune_backbone");p.stage="fine-tune backbone";p.FineTuneBackbone(features,head); }',
    '        if(p.backboneEpochs>0&&!deferInstance) { Impl::StageTimer timer(p,"finetune_backbone");p.stage="fine-tune backbone";p.FineTuneBackbone(features,head); }')

open(p, 'w', encoding='utf8', newline='').write(s)
print('patched', len(orig), '->', len(s))
