import React from 'react';
import type { RuntimeType } from '../../engine-contract';

export interface SvgIconProps {
  size?: number;
  className?: string;
}

type IconComponent = React.FC<SvgIconProps>;

/* Noop — empty circle */
const NoopIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <circle cx="12" cy="12" r="9" />
    <path d="M9 9l6 6M15 9l-6 6" />
  </svg>
);

/* CSharp — code block */
const CSharpIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <path d="M16 18l6-6-6-6" />
    <path d="M8 6l-6 6 6 6" />
  </svg>
);

/* CSharpScript — file with code */
const CSharpScriptIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
    <path d="M14 2v6h6" />
    <path d="M10 12l-2 2 2 2" />
    <path d="M14 12l2 2-2 2" />
  </svg>
);

/* CSharpTask — gear */
const CSharpTaskIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <rect x="3" y="3" width="18" height="18" rx="2" />
    <path d="M9 12h6" />
  </svg>
);

/* PowerShell — terminal */
const PowerShellIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <path d="M4 17l6-5-6-5" />
    <path d="M12 19h8" />
  </svg>
);

/* PowerShellTask — terminal + gear */
const PowerShellTaskIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <rect x="2" y="3" width="20" height="18" rx="2" />
    <path d="M6 12l4-3.5L6 5" />
    <path d="M12 15h6" />
  </svg>
);

/* IfElse — diamond / branch */
const IfElseIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <path d="M7 4v6a4 4 0 0 0 4 4h6M7 4H3M17 14l3-3-3-3M17 14v6h-4M17 14h4" />
  </svg>
);

/* ForEach — loop arrows */
const ForEachIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <path d="M17 1l4 4-4 4M3 11V9a4 4 0 0 1 4-4h14M7 23l-4-4 4-4M21 13v2a4 4 0 0 1-4 4H3" />
  </svg>
);

/* While — refresh cycle */
const WhileIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <path d="M1 4v6h6M3.51 15a9 9 0 1 0 2.13-9.36L1 10" />
  </svg>
);

/* Switch — multi-branch */
const SwitchIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <path d="M12 3v18M3 12h18M6 6l12 12M18 6 6 18" />
  </svg>
);

/* Subflow — 3D box */
const SubflowIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
    <path d="M3.27 6.96 12 12.01l8.73-5.05M12 22.08V12" />
  </svg>
);

/* Timer — clock */
const TimerIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <circle cx="12" cy="12" r="10" />
    <path d="M12 6v6l4 2" />
  </svg>
);

/* Container — dashed rectangle */
const ContainerIcon: IconComponent = ({ size = 14, className }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={className} aria-hidden="true">
    <rect x="3" y="3" width="18" height="18" rx="2" strokeDasharray="3 2" />
  </svg>
);

export const NODE_TYPE_ICONS: Record<RuntimeType, IconComponent> = {
  Noop: NoopIcon,
  CSharp: CSharpIcon,
  CSharpScript: CSharpScriptIcon,
  CSharpTask: CSharpTaskIcon,
  PowerShell: PowerShellIcon,
  PowerShellTask: PowerShellTaskIcon,
  IfElse: IfElseIcon,
  ForEach: ForEachIcon,
  While: WhileIcon,
  Switch: SwitchIcon,
  Subflow: SubflowIcon,
  Timer: TimerIcon,
  Container: ContainerIcon,
};

export function getNodeTypeIcon(runtimeType: RuntimeType): IconComponent {
  return NODE_TYPE_ICONS[runtimeType];
}
