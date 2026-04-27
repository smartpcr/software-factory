// AUTO-GENERATED — do not hand-edit.
// Source: agents/allocation.json (regenerate via scripts/gen-crew-allocation.py)

export type Team = 'Architect' | 'Engineer' | 'Review' | 'QA';

export interface AllocationEntry {
  id: string;
  name: string;
  /** Display label (e.g. "Product Strategist", "code-generator"). */
  role_label: string;
  /** Canonical team — one of Architect / Engineer / Review / QA. */
  team: Team;
  /** Pod within the team: leadership | plan-design | coding | ai-worker | reserve. */
  pod: string;
  /** Fine-grained bucket inside the pod (e.g. plan-red-team, code-judge). */
  role_bucket: string;
  /** Underlying Phase-11 crew role: arch | eng | rev | qa | product-owner. */
  crew_role: string;
  avatar: string | null;
}

export const AGENT_ALLOCATION: AllocationEntry[] = [
  {
    "id": "XD",
    "name": "Xiaodong",
    "role_label": "Product Strategist / Product Owner",
    "team": "Architect",
    "pod": "leadership-product",
    "role_bucket": "leadership",
    "crew_role": "product-owner",
    "avatar": null
  },
  {
    "id": "YS",
    "name": "Yuan Shi Tianzun",
    "role_label": "AI Factory Architect",
    "team": "Architect",
    "pod": "leadership-factory",
    "role_bucket": "leadership",
    "crew_role": "arch",
    "avatar": "06_Yuan_Shi_Tianzun.png"
  },
  {
    "id": "TY",
    "name": "Taiji Zhenren",
    "role_label": "Principal Engineer / Domain Expert",
    "team": "Architect",
    "pod": "leadership-principal",
    "role_bucket": "leadership",
    "crew_role": "arch",
    "avatar": "05_Taiji_Zhenren.png"
  },
  {
    "id": "WZ",
    "name": "Wen Zhong",
    "role_label": "Incident Commander / Reliability",
    "team": "Architect",
    "pod": "leadership-incident",
    "role_bucket": "leadership",
    "crew_role": "arch",
    "avatar": "84_Wen_Zhong.png"
  },
  {
    "id": "LJ",
    "name": "Li Jing",
    "role_label": "Security / Governance Lead",
    "team": "Architect",
    "pod": "leadership-security",
    "role_bucket": "leadership",
    "crew_role": "arch",
    "avatar": "03_Li_Jing.png"
  },
  {
    "id": "JZ",
    "name": "Jinzha",
    "role_label": "DevOps / Platform Lead",
    "team": "Engineer",
    "pod": "leadership-devops",
    "role_bucket": "leadership",
    "crew_role": "eng",
    "avatar": "38_Jinzha.png"
  },
  {
    "id": "SG",
    "name": "Shen Gongbao",
    "role_label": "Prompt / Agent Engineer Lead",
    "team": "Review",
    "pod": "leadership-prompt",
    "role_bucket": "leadership",
    "crew_role": "rev",
    "avatar": "10_Shen_Gongbao.png"
  },
  {
    "id": "SF",
    "name": "Shun Feng'er",
    "role_label": "QA / Validation Lead",
    "team": "QA",
    "pod": "leadership-qa",
    "role_bucket": "leadership",
    "crew_role": "qa",
    "avatar": "27_Shun_Fenger.png"
  },
  {
    "id": "NZ",
    "name": "Ne Zha",
    "role_label": "plan-architect-generator",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-architect-generator",
    "crew_role": "arch",
    "avatar": "01_Ne_Zha.png"
  },
  {
    "id": "AB",
    "name": "Ao Bing",
    "role_label": "plan-architect-generator",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-architect-generator",
    "crew_role": "arch",
    "avatar": "02_Ao_Bing.png"
  },
  {
    "id": "LY",
    "name": "Lady Yin",
    "role_label": "plan-architect-generator",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-architect-generator",
    "crew_role": "arch",
    "avatar": "04_Lady_Yin.png"
  },
  {
    "id": "SN",
    "name": "Shiji Niangniang",
    "role_label": "plan-architect-generator",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-architect-generator",
    "crew_role": "arch",
    "avatar": "07_Shiji_Niangniang.png"
  },
  {
    "id": "DH",
    "name": "Dong Hai Long Wang",
    "role_label": "plan-architect-generator",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-architect-generator",
    "crew_role": "arch",
    "avatar": "08_Dong_Hai_Long_Wang.png"
  },
  {
    "id": "TZ",
    "name": "Taiji Zhenren's Disciple",
    "role_label": "plan-architect-generator",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-architect-generator",
    "crew_role": "arch",
    "avatar": "09_Taiji_Zhenrens_Disciple.png"
  },
  {
    "id": "WU",
    "name": "Wuliang",
    "role_label": "plan-architect-generator",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-architect-generator",
    "crew_role": "arch",
    "avatar": "11_Wuliang.png"
  },
  {
    "id": "CL",
    "name": "Chentangguan Li Jing",
    "role_label": "plan-architect-generator",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-architect-generator",
    "crew_role": "arch",
    "avatar": "12_Chentangguan_Li_Jing.png"
  },
  {
    "id": "GZ",
    "name": "Guang Zong",
    "role_label": "plan-red-team",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-red-team",
    "crew_role": "arch",
    "avatar": "13_Guang_Zong.png"
  },
  {
    "id": "MY",
    "name": "Mo Yuan",
    "role_label": "plan-red-team",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-red-team",
    "crew_role": "arch",
    "avatar": "14_Mo_Yuan.png"
  },
  {
    "id": "MY2",
    "name": "Mo Yuxuan",
    "role_label": "plan-red-team",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-red-team",
    "crew_role": "arch",
    "avatar": "15_Mo_Yuxuan.png"
  },
  {
    "id": "XL",
    "name": "Xiao Longnv",
    "role_label": "plan-red-team",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-red-team",
    "crew_role": "arch",
    "avatar": "16_Xiao_Longnv.png"
  },
  {
    "id": "AG",
    "name": "Ao Guang",
    "role_label": "plan-red-team",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-red-team",
    "crew_role": "arch",
    "avatar": "17_Ao_Guang.png"
  },
  {
    "id": "BL",
    "name": "Bai Longma",
    "role_label": "plan-red-team",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-red-team",
    "crew_role": "arch",
    "avatar": "18_Bai_Longma.png"
  },
  {
    "id": "YL",
    "name": "Yinglong",
    "role_label": "plan-red-team",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-red-team",
    "crew_role": "arch",
    "avatar": "19_Yinglong.png"
  },
  {
    "id": "SG2",
    "name": "Shen Gongbao's Disciple",
    "role_label": "plan-red-team",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-red-team",
    "crew_role": "arch",
    "avatar": "20_Shen_Gongbaos_Disciple.png"
  },
  {
    "id": "LE",
    "name": "Leyuan",
    "role_label": "plan-refinement",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-refinement",
    "crew_role": "arch",
    "avatar": "21_Leyuan.png"
  },
  {
    "id": "YX",
    "name": "Yun Xiao",
    "role_label": "plan-refinement",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-refinement",
    "crew_role": "arch",
    "avatar": "22_Yun_Xiao.png"
  },
  {
    "id": "DJ",
    "name": "Da Ji",
    "role_label": "plan-refinement",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-refinement",
    "crew_role": "arch",
    "avatar": "23_Da_Ji.png"
  },
  {
    "id": "HD",
    "name": "Hun Dun",
    "role_label": "plan-refinement",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-refinement",
    "crew_role": "arch",
    "avatar": "24_Hun_Dun.png"
  },
  {
    "id": "QK",
    "name": "Qian Kun",
    "role_label": "plan-requirements-judge",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-requirements-judge",
    "crew_role": "arch",
    "avatar": "25_Qian_Kun.png"
  },
  {
    "id": "SH",
    "name": "Shan He",
    "role_label": "plan-requirements-judge",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-requirements-judge",
    "crew_role": "arch",
    "avatar": "26_Shan_He.png"
  },
  {
    "id": "SC",
    "name": "Shi Cheng",
    "role_label": "plan-requirements-judge",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-requirements-judge",
    "crew_role": "arch",
    "avatar": "28_Shi_Cheng.png"
  },
  {
    "id": "LJ2",
    "name": "Long Jiao Jiao",
    "role_label": "plan-requirements-judge",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-requirements-judge",
    "crew_role": "arch",
    "avatar": "29_Long_Jiao_Jiao.png"
  },
  {
    "id": "NZ2",
    "name": "Ne Zha (Spirits Form)",
    "role_label": "plan-design-historian",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-design-historian",
    "crew_role": "arch",
    "avatar": "30_Ne_Zha_Spirits_Form.png"
  },
  {
    "id": "AB2",
    "name": "Ao Bing (Dragon Form)",
    "role_label": "plan-design-historian",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-design-historian",
    "crew_role": "arch",
    "avatar": "31_Ao_Bing_Dragon_Form.png"
  },
  {
    "id": "LJ3",
    "name": "Li Jing (Young)",
    "role_label": "plan-design-historian",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-design-historian",
    "crew_role": "arch",
    "avatar": "32_Li_Jing_Young.png"
  },
  {
    "id": "LY2",
    "name": "Lady Yin (Young)",
    "role_label": "plan-orchestrator",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-orchestrator",
    "crew_role": "arch",
    "avatar": "33_Lady_Yin_Young.png"
  },
  {
    "id": "LM",
    "name": "Li Mu",
    "role_label": "plan-orchestrator",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-orchestrator",
    "crew_role": "arch",
    "avatar": "34_Li_Mu.png"
  },
  {
    "id": "YS2",
    "name": "Yin Shishi",
    "role_label": "plan-orchestrator",
    "team": "Architect",
    "pod": "plan-design",
    "role_bucket": "plan-orchestrator",
    "crew_role": "arch",
    "avatar": "35_Yin_Shishi.png"
  },
  {
    "id": "LZ",
    "name": "Ling Zhu",
    "role_label": "code-generator",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-generator",
    "crew_role": "eng",
    "avatar": "36_Ling_Zhu.png"
  },
  {
    "id": "HU",
    "name": "Hunjiang",
    "role_label": "code-generator",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-generator",
    "crew_role": "eng",
    "avatar": "37_Hunjiang.png"
  },
  {
    "id": "MB",
    "name": "Mu Bao",
    "role_label": "code-generator",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-generator",
    "crew_role": "eng",
    "avatar": "39_Mu_Bao.png"
  },
  {
    "id": "YG",
    "name": "Yuhe Gong",
    "role_label": "code-generator",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-generator",
    "crew_role": "eng",
    "avatar": "40_Yuhe_Gong.png"
  },
  {
    "id": "WJ",
    "name": "Wang Jing",
    "role_label": "code-generator",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-generator",
    "crew_role": "eng",
    "avatar": "41_Wang_Jing.png"
  },
  {
    "id": "ZC",
    "name": "Zhang Chulan",
    "role_label": "code-generator",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-generator",
    "crew_role": "eng",
    "avatar": "42_Zhang_Chulan.png"
  },
  {
    "id": "YS3",
    "name": "Yu Shi",
    "role_label": "code-generator",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-generator",
    "crew_role": "eng",
    "avatar": "43_Yu_Shi.png"
  },
  {
    "id": "CY",
    "name": "Cheng Yu",
    "role_label": "code-generator",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-generator",
    "crew_role": "eng",
    "avatar": "44_Cheng_Yu.png"
  },
  {
    "id": "BE",
    "name": "Bao Er",
    "role_label": "code-generator",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-generator",
    "crew_role": "eng",
    "avatar": "45_Bao_Er.png"
  },
  {
    "id": "LG",
    "name": "Lu Gui",
    "role_label": "code-generator",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-generator",
    "crew_role": "eng",
    "avatar": "46_Lu_Gui.png"
  },
  {
    "id": "HB",
    "name": "Han Bing",
    "role_label": "code-breaker",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-breaker",
    "crew_role": "eng",
    "avatar": "47_Han_Bing.png"
  },
  {
    "id": "LL",
    "name": "Li Lao",
    "role_label": "code-breaker",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-breaker",
    "crew_role": "eng",
    "avatar": "48_Li_Lao.png"
  },
  {
    "id": "XH",
    "name": "Xiao Han",
    "role_label": "code-breaker",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-breaker",
    "crew_role": "eng",
    "avatar": "49_Xiao_Han.png"
  },
  {
    "id": "JS",
    "name": "Jiao Shi",
    "role_label": "code-breaker",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-breaker",
    "crew_role": "eng",
    "avatar": "50_Jiao_Shi.png"
  },
  {
    "id": "YL3",
    "name": "Ye Lei",
    "role_label": "code-breaker",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-breaker",
    "crew_role": "eng",
    "avatar": "51_Ye_Lei.png"
  },
  {
    "id": "ZR",
    "name": "Zhu Rong",
    "role_label": "code-breaker",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-breaker",
    "crew_role": "eng",
    "avatar": "52_Zhu_Rong.png"
  },
  {
    "id": "GG",
    "name": "Gong Gong",
    "role_label": "code-breaker",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-breaker",
    "crew_role": "eng",
    "avatar": "53_Gong_Gong.png"
  },
  {
    "id": "HD2",
    "name": "Huo De",
    "role_label": "code-breaker",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-breaker",
    "crew_role": "eng",
    "avatar": "54_Huo_De.png"
  },
  {
    "id": "HL",
    "name": "Hao Long",
    "role_label": "code-unit-test-attacker",
    "team": "QA",
    "pod": "coding",
    "role_bucket": "code-unit-test-attacker",
    "crew_role": "qa",
    "avatar": "55_Hao_Long.png"
  },
  {
    "id": "DC",
    "name": "Deng Chanyu",
    "role_label": "code-unit-test-attacker",
    "team": "QA",
    "pod": "coding",
    "role_bucket": "code-unit-test-attacker",
    "crew_role": "qa",
    "avatar": "56_Deng_Chanyu.png"
  },
  {
    "id": "LI",
    "name": "Liuba",
    "role_label": "code-unit-test-attacker",
    "team": "QA",
    "pod": "coding",
    "role_bucket": "code-unit-test-attacker",
    "crew_role": "qa",
    "avatar": "57_Liuba.png"
  },
  {
    "id": "WG",
    "name": "Wan Gui",
    "role_label": "code-unit-test-attacker",
    "team": "QA",
    "pod": "coding",
    "role_bucket": "code-unit-test-attacker",
    "crew_role": "qa",
    "avatar": "58_Wan_Gui.png"
  },
  {
    "id": "SD",
    "name": "Shui De",
    "role_label": "code-unit-test-attacker",
    "team": "QA",
    "pod": "coding",
    "role_bucket": "code-unit-test-attacker",
    "crew_role": "qa",
    "avatar": "59_Shui_De.png"
  },
  {
    "id": "HD3",
    "name": "He De",
    "role_label": "code-refactor-defender",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-refactor-defender",
    "crew_role": "eng",
    "avatar": "60_He_De.png"
  },
  {
    "id": "FL",
    "name": "Fei Lian",
    "role_label": "code-refactor-defender",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-refactor-defender",
    "crew_role": "eng",
    "avatar": "61_Fei_Lian.png"
  },
  {
    "id": "YS4",
    "name": "Yun Shen",
    "role_label": "code-refactor-defender",
    "team": "Engineer",
    "pod": "coding",
    "role_bucket": "code-refactor-defender",
    "crew_role": "eng",
    "avatar": "62_Yun_Shen.png"
  },
  {
    "id": "XY",
    "name": "Xu You",
    "role_label": "code-judge",
    "team": "Review",
    "pod": "coding",
    "role_bucket": "code-judge",
    "crew_role": "rev",
    "avatar": "63_Xu_You.png"
  },
  {
    "id": "QG",
    "name": "Qin Guang",
    "role_label": "code-judge",
    "team": "Review",
    "pod": "coding",
    "role_bucket": "code-judge",
    "crew_role": "rev",
    "avatar": "64_Qin_Guang.png"
  },
  {
    "id": "JS2",
    "name": "Jin Si",
    "role_label": "code-memory-agent",
    "team": "QA",
    "pod": "coding",
    "role_bucket": "code-memory-agent",
    "crew_role": "qa",
    "avatar": "65_Jin_Si.png"
  },
  {
    "id": "JT",
    "name": "Jiu Tou Chong (Nine-headed)",
    "role_label": "code-memory-agent",
    "team": "QA",
    "pod": "coding",
    "role_bucket": "code-memory-agent",
    "crew_role": "qa",
    "avatar": "66_Jiu_Tou_Chong_Nine_headed.png"
  },
  {
    "id": "TF",
    "name": "Tie Fan Li",
    "role_label": "ai-requirements-analyst",
    "team": "Architect",
    "pod": "ai-worker",
    "role_bucket": "ai-requirements-analyst",
    "crew_role": "arch",
    "avatar": "67_Tie_Fan_Li.png"
  },
  {
    "id": "SD2",
    "name": "Shuiguan Daxiang",
    "role_label": "ai-ux-designer",
    "team": "Engineer",
    "pod": "ai-worker",
    "role_bucket": "ai-ux-designer",
    "crew_role": "eng",
    "avatar": "68_Shuiguan_Daxiang.png"
  },
  {
    "id": "JC",
    "name": "Jin Chanzi",
    "role_label": "ai-systems-architect",
    "team": "Architect",
    "pod": "ai-worker",
    "role_bucket": "ai-systems-architect",
    "crew_role": "arch",
    "avatar": "69_Jin_Chanzi.png"
  },
  {
    "id": "SA",
    "name": "Sanhua",
    "role_label": "ai-backend-engineer",
    "team": "Engineer",
    "pod": "ai-worker",
    "role_bucket": "ai-backend-engineer",
    "crew_role": "eng",
    "avatar": "70_Sanhua.png"
  },
  {
    "id": "YA",
    "name": "Yazi",
    "role_label": "ai-frontend-engineer",
    "team": "Engineer",
    "pod": "ai-worker",
    "role_bucket": "ai-frontend-engineer",
    "crew_role": "eng",
    "avatar": "71_Yazi.png"
  },
  {
    "id": "XH2",
    "name": "Xiong He",
    "role_label": "ai-test-engineer",
    "team": "QA",
    "pod": "ai-worker",
    "role_bucket": "ai-test-engineer",
    "crew_role": "qa",
    "avatar": "72_Xiong_He.png"
  },
  {
    "id": "BY",
    "name": "Bing Yi",
    "role_label": "ai-security-reviewer",
    "team": "Review",
    "pod": "ai-worker",
    "role_bucket": "ai-security-reviewer",
    "crew_role": "rev",
    "avatar": "73_Bing_Yi.png"
  },
  {
    "id": "QS",
    "name": "Qiu Shuang",
    "role_label": "ai-code-reviewer",
    "team": "Review",
    "pod": "ai-worker",
    "role_bucket": "ai-code-reviewer",
    "crew_role": "rev",
    "avatar": "74_Qiu_Shuang.png"
  },
  {
    "id": "YL2",
    "name": "Xiao Yue Lao",
    "role_label": "ai-release-manager",
    "team": "Engineer",
    "pod": "ai-worker",
    "role_bucket": "ai-release-manager",
    "crew_role": "eng",
    "avatar": "75_Xiao_Yue_Lao.png"
  },
  {
    "id": "ZZ",
    "name": "Zhi Zhi",
    "role_label": "ai-sre",
    "team": "QA",
    "pod": "ai-worker",
    "role_bucket": "ai-sre",
    "crew_role": "qa",
    "avatar": "76_Zhi_Zhi.png"
  },
  {
    "id": "AL",
    "name": "A Lang",
    "role_label": "ai-customer-support",
    "team": "Review",
    "pod": "ai-worker",
    "role_bucket": "ai-customer-support",
    "crew_role": "rev",
    "avatar": "77_A_Lang.png"
  },
  {
    "id": "XQ",
    "name": "Xiao Qiang",
    "role_label": "ai-root-cause-investigator",
    "team": "QA",
    "pod": "ai-worker",
    "role_bucket": "ai-root-cause-investigator",
    "crew_role": "qa",
    "avatar": "78_Xiao_Qiang.png"
  },
  {
    "id": "DJ2",
    "name": "Dian Jiang",
    "role_label": "ai-doc-writer",
    "team": "Review",
    "pod": "ai-worker",
    "role_bucket": "ai-doc-writer",
    "crew_role": "rev",
    "avatar": "79_Dian_Jiang.png"
  },
  {
    "id": "JI",
    "name": "Jingang",
    "role_label": "reserve",
    "team": "Engineer",
    "pod": "reserve",
    "role_bucket": "reserve",
    "crew_role": "eng",
    "avatar": "80_Jingang.png"
  },
  {
    "id": "ML",
    "name": "Mang Liu",
    "role_label": "reserve",
    "team": "Engineer",
    "pod": "reserve",
    "role_bucket": "reserve",
    "crew_role": "eng",
    "avatar": "81_Mang_Liu.png"
  },
  {
    "id": "YG2",
    "name": "Yao Gan",
    "role_label": "reserve",
    "team": "Engineer",
    "pod": "reserve",
    "role_bucket": "reserve",
    "crew_role": "eng",
    "avatar": "82_Yao_Gan.png"
  },
  {
    "id": "BG",
    "name": "Bi Gan",
    "role_label": "reserve",
    "team": "Engineer",
    "pod": "reserve",
    "role_bucket": "reserve",
    "crew_role": "eng",
    "avatar": "83_Bi_Gan.png"
  },
  {
    "id": "CL2",
    "name": "Chen Lin",
    "role_label": "reserve",
    "team": "Engineer",
    "pod": "reserve",
    "role_bucket": "reserve",
    "crew_role": "eng",
    "avatar": "85_Chen_Lin.png"
  },
  {
    "id": "LC",
    "name": "Lu Cheng",
    "role_label": "reserve",
    "team": "Engineer",
    "pod": "reserve",
    "role_bucket": "reserve",
    "crew_role": "eng",
    "avatar": "86_Lu_Cheng.png"
  },
  {
    "id": "ZB",
    "name": "Zhu Baji",
    "role_label": "reserve",
    "team": "Engineer",
    "pod": "reserve",
    "role_bucket": "reserve",
    "crew_role": "eng",
    "avatar": "87_Zhu_Baji.png"
  },
  {
    "id": "XQ2",
    "name": "Xiao Qing",
    "role_label": "reserve",
    "team": "Engineer",
    "pod": "reserve",
    "role_bucket": "reserve",
    "crew_role": "eng",
    "avatar": "88_Xiao_Qing.png"
  },
  {
    "id": "BH",
    "name": "Bei Hai Long Wang",
    "role_label": "reserve",
    "team": "Engineer",
    "pod": "reserve",
    "role_bucket": "reserve",
    "crew_role": "eng",
    "avatar": "89_Bei_Hai_Long_Wang.png"
  },
  {
    "id": "ZK",
    "name": "Zhong Kui",
    "role_label": "reserve",
    "team": "Engineer",
    "pod": "reserve",
    "role_bucket": "reserve",
    "crew_role": "eng",
    "avatar": "90_Zhong_Kui.png"
  }
];
