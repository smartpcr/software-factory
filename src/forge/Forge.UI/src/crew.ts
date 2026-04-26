// Agent crew definitions. Each entry binds a Ne Zha character (image
// asset under src/assets/crews) to a Forge role + capability list.
// The role/capability mapping is editorial — pick characters whose
// canonical traits match the kind of work each role does.

import type { OverviewAgent } from './types';

import imgTaijiZhenren    from '@/assets/crews/05_Taiji_Zhenren.png';
import imgYuanShi         from '@/assets/crews/06_Yuan_Shi_Tianzun.png';
import imgLiJing          from '@/assets/crews/03_Li_Jing.png';
import imgWenZhong        from '@/assets/crews/84_Wen_Zhong.png';
import imgBiGan           from '@/assets/crews/83_Bi_Gan.png';

import imgNeZha           from '@/assets/crews/01_Ne_Zha.png';
import imgAoBing          from '@/assets/crews/02_Ao_Bing.png';
import imgZhuRong         from '@/assets/crews/52_Zhu_Rong.png';
import imgGongGong        from '@/assets/crews/53_Gong_Gong.png';
import imgYinglong        from '@/assets/crews/19_Yinglong.png';
import imgMuBao           from '@/assets/crews/39_Mu_Bao.png';
import imgJinzha          from '@/assets/crews/38_Jinzha.png';

import imgShenGongbao     from '@/assets/crews/10_Shen_Gongbao.png';
import imgYunXiao         from '@/assets/crews/22_Yun_Xiao.png';
import imgXiaoYueLao      from '@/assets/crews/75_Xiao_Yue_Lao.png';

import imgShunFenger      from '@/assets/crews/27_Shun_Fenger.png';
import imgQianKun         from '@/assets/crews/25_Qian_Kun.png';
import imgHunDun          from '@/assets/crews/24_Hun_Dun.png';
import imgZhiZhi          from '@/assets/crews/76_Zhi_Zhi.png';

export const crew: OverviewAgent[] = [
  // -- Architects: sages, planners, governance --
  {
    id: 'TY', name: 'Taiji Zhenren', role: 'arch', status: 'working',
    task: 'ADR-021 log rotation policy', avatar: imgTaijiZhenren,
    capabilities: ['system design', 'mentoring', 'release coordination'],
    bio: 'Taoist immortal mentor — frames the problem before the team swings at it.',
  },
  {
    id: 'YS', name: 'Yuan Shi Tianzun', role: 'arch', status: 'working',
    task: 'platform charter v3', avatar: imgYuanShi,
    capabilities: ['governance', 'platform strategy', 'org-wide ADRs'],
    bio: 'Supreme deity of the Taoist pantheon — owns the cross-cutting "why".',
  },
  {
    id: 'LJ', name: 'Li Jing', role: 'arch', status: 'idle',
    task: '—', avatar: imgLiJing,
    capabilities: ['security architecture', 'isolation', 'tenancy'],
    bio: 'The Pagoda-Bearing General — keeps blast radii small and contained.',
  },
  {
    id: 'WZ', name: 'Wen Zhong', role: 'arch', status: 'working',
    task: '#820 OTel propagation', avatar: imgWenZhong,
    capabilities: ['capacity planning', 'incident command', 'observability'],
    bio: 'Grand Tutor & strategist — runs war-rooms and reads the smoke signals.',
  },
  {
    id: 'BG', name: 'Bi Gan', role: 'arch', status: 'idle',
    task: '—', avatar: imgBiGan,
    capabilities: ['integrity audit', 'data lineage', 'compliance'],
    bio: 'The legendary loyal minister — surfaces uncomfortable truths in the data.',
  },

  // -- Engineers: build, ship, optimise --
  {
    id: 'NZ', name: 'Ne Zha', role: 'eng', status: 'working',
    task: '#234 OIDC rotation (build)', avatar: imgNeZha,
    capabilities: ['feature dev', 'perf tuning', 'Rust', 'auth'],
    bio: 'Untamed prodigy — fast, confident, occasionally needs a code reviewer.',
  },
  {
    id: 'AB', name: 'Ao Bing', role: 'eng', status: 'working',
    task: '#237 retry & jitter', avatar: imgAoBing,
    capabilities: ['distributed systems', 'refactoring', 'Go'],
    bio: 'Dragon prince — measured, cool-headed counterweight to Ne Zha.',
  },
  {
    id: 'ZR', name: 'Zhu Rong', role: 'eng', status: 'working',
    task: '#241 per-tenant rate limit', avatar: imgZhuRong,
    capabilities: ['backend services', 'build engines', 'C++'],
    bio: 'Fire god — runs hot at the runtime and toolchain layer.',
  },
  {
    id: 'GG', name: 'Gong Gong', role: 'eng', status: 'working',
    task: '#812 streaming exporter', avatar: imgGongGong,
    capabilities: ['data pipelines', 'streaming', 'Kafka'],
    bio: 'Water god — moves bytes downstream, never stagnant.',
  },
  {
    id: 'YL', name: 'Yinglong', role: 'eng', status: 'queued',
    task: 'awaiting #822 schema', avatar: imgYinglong,
    capabilities: ['API gateway', 'networking', 'gRPC'],
    bio: 'Winged dragon — carries traffic across the boundary.',
  },
  {
    id: 'MB', name: 'Mu Bao', role: 'eng', status: 'working',
    task: 'Forge UI polish', avatar: imgMuBao,
    capabilities: ['frontend', 'TypeScript', 'animations'],
    bio: 'Younger sibling on the crew — owns the user-facing surface.',
  },
  {
    id: 'JZ', name: 'Jinzha', role: 'eng', status: 'idle',
    task: '—', avatar: imgJinzha,
    capabilities: ['IaC', 'k8s', 'platform plumbing'],
    bio: 'Disciplined elder brother — the cluster runs because Jinzha runs the cluster.',
  },

  // -- Reviewers: critique, security, taste --
  {
    id: 'SG', name: 'Shen Gongbao', role: 'rev', status: 'working',
    task: 'PR #240 security review', avatar: imgShenGongbao,
    capabilities: ['security review', 'threat modeling', 'auth'],
    bio: 'The cunning critic — finds the bypass nobody else thought to test.',
  },
  {
    id: 'YX', name: 'Yun Xiao', role: 'rev', status: 'working',
    task: 'PR #241 API design review', avatar: imgYunXiao,
    capabilities: ['API design', 'taste', 'docs'],
    bio: 'Reads PRs the way she reads characters — for shape and intent.',
  },
  {
    id: 'YL2', name: 'Xiao Yue Lao', role: 'rev', status: 'idle',
    task: '—', avatar: imgXiaoYueLao,
    capabilities: ['dependency review', 'license audit', 'SBOM'],
    bio: 'Old Man Under the Moon — careful with every binding he ties.',
  },

  // -- QA: verify, fuzz, watch the dials --
  {
    id: 'SF', name: "Shun Feng'er", role: 'qa', status: 'working',
    task: 'PR #229 soak verification', avatar: imgShunFenger,
    capabilities: ['observability', 'log analysis', 'SLO watching'],
    bio: 'Hears every distant sound — first to notice latency drift.',
  },
  {
    id: 'QK', name: 'Qian Kun', role: 'qa', status: 'idle',
    task: '—', avatar: imgQianKun,
    capabilities: ['end-to-end tests', 'Playwright', 'regression'],
    bio: 'Cosmic ring of Qian Kun — closes the loop on every code path.',
  },
  {
    id: 'HD', name: 'Hun Dun', role: 'qa', status: 'queued',
    task: 'chaos drill schedule', avatar: imgHunDun,
    capabilities: ['chaos engineering', 'fault injection'],
    bio: 'Primal chaos — schedules controlled blast radii in staging.',
  },
  {
    id: 'ZZ', name: 'Zhi Zhi', role: 'qa', status: 'working',
    task: 'fuzz protobuf parser', avatar: imgZhiZhi,
    capabilities: ['fuzzing', 'property-based tests', 'unsafe inputs'],
    bio: 'Persistent and patient — drives parsers to their crash logs.',
  },
];
