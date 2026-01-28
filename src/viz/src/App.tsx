// NVL Graph Demo Application
// Demonstrates all features of the library

import React, { useRef, useState, useCallback, useMemo } from 'react';
import { Chart, ChartRef, GraphNode, GraphEdge, LayoutType, SNAMetrics } from './components/Graph';
import { samples, getSampleList, SampleKey, SampleData } from './samples';

// Generate sample social network data
function generateSampleData(): { nodes: GraphNode[]; edges: GraphEdge[] } {

  const nodes: GraphNode[] = [
    { id: 'ceo', label: 'CEO', icon: '👔', color: '#6366f1', size: 80, data: { department: 'Executive' } },
    { id: 'cto', label: 'CTO', icon: '💻', color: '#10b981', size: 70, data: { department: 'Engineering' } },
    { id: 'cfo', label: 'CFO', icon: '💰', color: '#f59e0b', size: 70, data: { department: 'Finance' } },
    { id: 'hr', label: 'HR Director', icon: '👥', color: '#ec4899', size: 60, data: { department: 'HR' } },
    { id: 'eng1', label: 'Lead Engineer', icon: '⚙️', color: '#10b981', size: 55, data: { department: 'Engineering' } },
    { id: 'eng2', label: 'Senior Dev', icon: '🔧', color: '#10b981', size: 50, data: { department: 'Engineering' } },
    { id: 'eng3', label: 'Junior Dev', icon: '📝', color: '#10b981', size: 45, data: { department: 'Engineering' } },
    { id: 'design', label: 'Designer', icon: '🎨', color: '#8b5cf6', size: 50, data: { department: 'Product' } },
    { id: 'pm', label: 'Product Manager', icon: '📋', color: '#06b6d4', size: 55, data: { department: 'Product' } },
    { id: 'sales1', label: 'Sales Lead', icon: '📈', color: '#f59e0b', size: 55, data: { department: 'Sales' } },
    { id: 'sales2', label: 'Account Exec', icon: '🤝', color: '#f59e0b', size: 50, data: { department: 'Sales' } },
    { id: 'marketing', label: 'Marketing', icon: '📣', color: '#ef4444', size: 55, data: { department: 'Marketing' } },
    { id: 'support', label: 'Support', icon: '🎧', color: '#84cc16', size: 50, data: { department: 'Support' } },
    { id: 'intern1', label: 'Intern 1', icon: '🎓', color: '#94a3b8', size: 40, data: { department: 'HR' } },
    { id: 'intern2', label: 'Intern 2', icon: '🎓', color: '#94a3b8', size: 40, data: { department: 'Marketing' } },
  ];

  // Add timestamps for time bar demo
  const now = Date.now();
  const day = 24 * 60 * 60 * 1000;
  nodes.forEach((node) => {
    node.timestamp = now - Math.random() * 30 * day;
  });

  // Add glyphs to some nodes
  nodes[0].glyphs = [
    { position: 'topRight', color: '#22c55e', text: '✓' },
    { position: 'bottomRight', color: '#3b82f6', text: '5' },
  ];
  nodes[1].glyphs = [{ position: 'topRight', color: '#ef4444', text: '!' }];

  const edges: GraphEdge[] = [
    // CEO connections
    { id: 'e1', source: 'ceo', target: 'cto', label: 'manages' },
    { id: 'e2', source: 'ceo', target: 'cfo', label: 'manages' },
    { id: 'e3', source: 'ceo', target: 'hr', label: 'manages' },
    
    // CTO's team
    { id: 'e4', source: 'cto', target: 'eng1', label: 'leads' },
    { id: 'e5', source: 'cto', target: 'pm', label: 'works with' },
    { id: 'e6', source: 'eng1', target: 'eng2' },
    { id: 'e7', source: 'eng1', target: 'eng3' },
    { id: 'e8', source: 'eng2', target: 'eng3', label: 'mentors' },
    { id: 'e9', source: 'pm', target: 'design', label: 'collaborates' },
    { id: 'e10', source: 'design', target: 'eng2', label: 'designs for' },
    
    // CFO's team
    { id: 'e11', source: 'cfo', target: 'sales1' },
    { id: 'e12', source: 'sales1', target: 'sales2' },
    { id: 'e13', source: 'cfo', target: 'marketing' },
    
    // Cross-functional
    { id: 'e14', source: 'marketing', target: 'sales1', style: 'dashed' },
    { id: 'e15', source: 'pm', target: 'marketing', style: 'dashed' },
    { id: 'e16', source: 'support', target: 'sales2', style: 'dotted' },
    { id: 'e17', source: 'eng1', target: 'support', style: 'dotted' },
    
    // HR connections
    { id: 'e18', source: 'hr', target: 'intern1' },
    { id: 'e19', source: 'hr', target: 'intern2' },
    { id: 'e20', source: 'intern1', target: 'eng3', label: 'assists' },
    { id: 'e21', source: 'intern2', target: 'marketing', label: 'assists' },
  ];

  // Add timestamps to edges
  edges.forEach((edge) => {
    edge.timestamp = now - Math.random() * 30 * day;
  });

  return { nodes, edges };
}

// Toolbar component
interface ToolbarProps {
  onLayoutChange: (layout: LayoutType) => void;
  onColorByMetric: (metric: keyof SNAMetrics) => void;
  onColorByCommunity: () => void;
  onClearHighlight: () => void;
  onFitView: () => void;
  onFindPath: () => void;
  onScaleByConnections: (enabled: boolean) => void;
  onGroupByAttribute: (attribute: string) => void;
  onUngroupAll: () => void;
  onResetColors: () => void;
  theme: 'light' | 'dark';
  onThemeChange: (theme: 'light' | 'dark') => void;
  currentLayout: LayoutType;
  scaledByConnections: boolean;
  groupedBy: string | null;
}

const Toolbar: React.FC<ToolbarProps> = ({
  onLayoutChange,
  onColorByMetric,
  onColorByCommunity,
  onClearHighlight,
  onFitView,
  onFindPath,
  onScaleByConnections,
  onGroupByAttribute,
  onUngroupAll,
  onResetColors,
  theme,
  onThemeChange,
  currentLayout,
  scaledByConnections,
  groupedBy,
}) => {
  const buttonStyle: React.CSSProperties = {
    padding: '8px 12px',
    borderRadius: 6,
    border: 'none',
    background: theme === 'dark' ? '#374151' : '#f3f4f6',
    color: theme === 'dark' ? '#e5e7eb' : '#374151',
    fontSize: 12,
    fontWeight: 500,
    cursor: 'pointer',
    transition: 'all 0.2s',
  };

  const selectStyle: React.CSSProperties = {
    ...buttonStyle,
    appearance: 'none' as const,
    paddingRight: 28,
    backgroundImage: `url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' fill='none' viewBox='0 0 24 24' stroke='%239ca3af'%3E%3Cpath stroke-linecap='round' stroke-linejoin='round' stroke-width='2' d='M19 9l-7 7-7-7'%3E%3C/path%3E%3C/svg%3E")`,
    backgroundRepeat: 'no-repeat',
    backgroundPosition: 'right 8px center',
    backgroundSize: '16px',
  };

  // All layouts that NVL supports
  const layouts: LayoutType[] = [
    'force',        // NVL's main force-directed layout
    'hierarchical', // Dagre-based hierarchical (best for edge crossing reduction)
    'grid',         // Regular grid arrangement
    'd3Force',      // D3's force simulation
  ];

  const metrics: Array<keyof SNAMetrics> = [
    'degree',
    'betweenness',
    'closeness',
    'pageRank',
    'clustering',
  ];

  return (
    <div
      style={{
        display: 'flex',
        gap: 8,
        padding: 12,
        background: theme === 'dark' ? '#1f2937' : 'white',
        borderBottom: `1px solid ${theme === 'dark' ? '#374151' : '#e5e7eb'}`,
        flexWrap: 'wrap',
        alignItems: 'center',
      }}
    >
      {/* Layout selector */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        <span
          style={{
            fontSize: 11,
            fontWeight: 600,
            color: theme === 'dark' ? '#9ca3af' : '#6b7280',
          }}
        >
          Layout:
        </span>
        <select
          value={currentLayout}
          onChange={(e) => onLayoutChange(e.target.value as LayoutType)}
          style={selectStyle}
        >
          {layouts.map((layout) => (
            <option key={layout} value={layout}>
              {layout.charAt(0).toUpperCase() + layout.slice(1)}
            </option>
          ))}
        </select>
      </div>

      <div
        style={{
          width: 1,
          height: 24,
          background: theme === 'dark' ? '#374151' : '#e5e7eb',
        }}
      />

      {/* Color by metric */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        <span
          style={{
            fontSize: 11,
            fontWeight: 600,
            color: theme === 'dark' ? '#9ca3af' : '#6b7280',
          }}
        >
          Color by:
        </span>
        <select
          onChange={(e) => {
            if (e.target.value === 'community') {
              onColorByCommunity();
            } else if (e.target.value === '') {
              onResetColors();
            } else if (e.target.value) {
              onColorByMetric(e.target.value as keyof SNAMetrics);
            }
          }}
          style={selectStyle}
          defaultValue=""
        >
          <option value="">None</option>
          {metrics.map((metric) => (
            <option key={metric} value={metric}>
              {metric.charAt(0).toUpperCase() + metric.slice(1)}
            </option>
          ))}
          <option value="community">Community</option>
        </select>
      </div>

      <div
        style={{
          width: 1,
          height: 24,
          background: theme === 'dark' ? '#374151' : '#e5e7eb',
        }}
      />

      {/* Action buttons */}
      <button style={buttonStyle} onClick={onFindPath}>
        🔍 Find Path
      </button>
      <button style={buttonStyle} onClick={onClearHighlight}>
        ✨ Clear Highlight
      </button>
      <button style={buttonStyle} onClick={onFitView}>
        🎯 Fit View
      </button>

      <div
        style={{
          width: 1,
          height: 24,
          background: theme === 'dark' ? '#374151' : '#e5e7eb',
        }}
      />

      {/* Scale by connections toggle */}
      <button
        style={{
          ...buttonStyle,
          background: scaledByConnections
            ? (theme === 'dark' ? '#4f46e5' : '#6366f1')
            : buttonStyle.background,
          color: scaledByConnections ? 'white' : buttonStyle.color,
        }}
        onClick={() => onScaleByConnections(!scaledByConnections)}
      >
        📐 Scale by Connections
      </button>

      {/* Group by attribute */}
      {groupedBy ? (
        <button style={buttonStyle} onClick={onUngroupAll}>
          📂 Ungroup ({groupedBy})
        </button>
      ) : (
        <button style={buttonStyle} onClick={() => onGroupByAttribute('department')}>
          📁 Group by Dept
        </button>
      )}

      <div style={{ flex: 1 }} />

      {/* Theme toggle */}
      <button
        style={buttonStyle}
        onClick={() => onThemeChange(theme === 'light' ? 'dark' : 'light')}
      >
        {theme === 'light' ? '🌙' : '☀️'} {theme === 'light' ? 'Dark' : 'Light'}
      </button>
    </div>
  );
};

// Info panel component
interface InfoPanelProps {
  selectedNode: GraphNode | null;
  metrics: SNAMetrics | null;
  theme: 'light' | 'dark';
}

const InfoPanel: React.FC<InfoPanelProps> = ({ selectedNode, metrics, theme }) => {
  if (!selectedNode) return null;

  const panelStyle: React.CSSProperties = {
    position: 'absolute',
    top: 16,
    left: 16,
    width: 250,
    padding: 16,
    background: theme === 'dark' ? '#1f2937' : 'white',
    borderRadius: 12,
    boxShadow: '0 4px 20px rgba(0, 0, 0, 0.15)',
    color: theme === 'dark' ? '#e5e7eb' : '#1f2937',
    fontSize: 13,
    zIndex: 100,
  };

  const labelStyle: React.CSSProperties = {
    fontSize: 11,
    color: theme === 'dark' ? '#9ca3af' : '#6b7280',
    marginBottom: 2,
  };

  const valueStyle: React.CSSProperties = {
    fontWeight: 600,
    marginBottom: 12,
  };

  const getMetric = (key: keyof SNAMetrics) => {
    return metrics?.[key]?.get(selectedNode.id)?.toFixed(4) || 'N/A';
  };

  return (
    <div style={panelStyle}>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 12,
          marginBottom: 16,
          paddingBottom: 12,
          borderBottom: `1px solid ${theme === 'dark' ? '#374151' : '#e5e7eb'}`,
        }}
      >
        <div
          style={{
            width: 48,
            height: 48,
            borderRadius: '50%',
            background: selectedNode.color || '#6366f1',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            fontSize: 24,
          }}
        >
          {selectedNode.icon || '👤'}
        </div>
        <div>
          <div style={{ fontWeight: 700, fontSize: 16 }}>
            {selectedNode.label || selectedNode.id}
          </div>
          <div style={{ fontSize: 11, color: theme === 'dark' ? '#9ca3af' : '#6b7280' }}>
            ID: {selectedNode.id}
          </div>
        </div>
      </div>

      <div style={labelStyle}>Degree Centrality</div>
      <div style={valueStyle}>{getMetric('degree')}</div>

      <div style={labelStyle}>Betweenness Centrality</div>
      <div style={valueStyle}>{getMetric('betweenness')}</div>

      <div style={labelStyle}>Closeness Centrality</div>
      <div style={valueStyle}>{getMetric('closeness')}</div>

      <div style={labelStyle}>PageRank</div>
      <div style={valueStyle}>{getMetric('pageRank')}</div>

      <div style={labelStyle}>Clustering Coefficient</div>
      <div style={{ fontWeight: 600 }}>{getMetric('clustering')}</div>
    </div>
  );
};

// Main App component
export default function App() {
  const chartRef = useRef<ChartRef>(null);
  const [currentSample, setCurrentSample] = useState<SampleKey | 'default'>('default');
  const [theme, setTheme] = useState<'light' | 'dark'>('light');
  const [currentLayout, setCurrentLayout] = useState<LayoutType>('hierarchical');
  const [selectedNode, setSelectedNode] = useState<GraphNode | null>(null);
  const [showTimeBar, setShowTimeBar] = useState(true);
  const [scaledByConnections, setScaledByConnections] = useState(false);
  const [groupedBy, setGroupedBy] = useState<string | null>(null);

  // Get sample data based on selection
  const data = useMemo(() => {
    if (currentSample === 'default') {
      return generateSampleData();
    }
    const sample = samples[currentSample]();
    return { nodes: sample.nodes, edges: sample.edges };
  }, [currentSample]);

  const sampleList = useMemo(() => getSampleList(), []);

  const handleSampleChange = useCallback((sampleKey: SampleKey | 'default') => {
    setCurrentSample(sampleKey);
    setSelectedNode(null);
    setScaledByConnections(false);
    setGroupedBy(null);
    // Reset to force layout for larger samples
    if (sampleKey !== 'default') {
      const sample = samples[sampleKey]();
      if (sample.nodes.length > 50) {
        setCurrentLayout('force');
      }
    }
  }, []);

  const handleLayoutChange = useCallback((layout: LayoutType) => {
    setCurrentLayout(layout);
    chartRef.current?.setLayout({ type: layout, direction: 'TB' });
  }, []);

  const handleColorByMetric = useCallback((metric: keyof SNAMetrics) => {
    chartRef.current?.colorByMetric(metric);
  }, []);

  const handleColorByCommunity = useCallback(() => {
    chartRef.current?.colorByCommunity();
  }, []);

  const handleClearHighlight = useCallback(() => {
    chartRef.current?.clearHighlight();
    setSelectedNode(null); // Also close the info popup
  }, []);

  const handleFitView = useCallback(() => {
    chartRef.current?.fitView();
  }, []);

  const handleFindPath = useCallback(() => {
    // Demo: find path between CEO and a junior dev
    chartRef.current?.highlightPath('ceo', 'eng3');
  }, []);

  const handleNodeClick = useCallback((node: GraphNode) => {
    setSelectedNode(node);
  }, []);

  const handleScaleByConnections = useCallback((enabled: boolean) => {
    setScaledByConnections(enabled);
    chartRef.current?.scaleByConnections(enabled);
  }, []);

  const handleGroupByAttribute = useCallback((attribute: string) => {
    setGroupedBy(attribute);
    chartRef.current?.groupByAttribute(attribute);
  }, []);

  const handleUngroupAll = useCallback(() => {
    setGroupedBy(null);
    chartRef.current?.ungroupAll();
  }, []);

  const handleResetColors = useCallback(() => {
    chartRef.current?.ungroupAll();
  }, []);

  return (
    <div
      style={{
        width: '100vw',
        height: '100vh',
        display: 'flex',
        flexDirection: 'column',
        background: theme === 'dark' ? '#111827' : '#f9fafb',
        fontFamily:
          '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
      }}
    >
      {/* Header */}
      <div
        style={{
          padding: '12px 16px',
          background: theme === 'dark' ? '#1f2937' : 'white',
          borderBottom: `1px solid ${theme === 'dark' ? '#374151' : '#e5e7eb'}`,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <span style={{ fontSize: 24 }}>📊</span>
          <div>
            <h1
              style={{
                margin: 0,
                fontSize: 18,
                fontWeight: 700,
                color: theme === 'dark' ? '#f3f4f6' : '#1f2937',
              }}
            >
              NVL Graph
            </h1>
            <p
              style={{
                margin: 0,
                fontSize: 12,
                color: theme === 'dark' ? '#9ca3af' : '#6b7280',
              }}
            >
              Built with Neo4j NVL
            </p>
          </div>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
          {/* Sample selector */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span
              style={{
                fontSize: 12,
                fontWeight: 600,
                color: theme === 'dark' ? '#9ca3af' : '#6b7280',
              }}
            >
              Dataset:
            </span>
            <select
              value={currentSample}
              onChange={(e) => handleSampleChange(e.target.value as SampleKey | 'default')}
              style={{
                padding: '6px 12px',
                borderRadius: 6,
                border: `1px solid ${theme === 'dark' ? '#374151' : '#e5e7eb'}`,
                background: theme === 'dark' ? '#374151' : '#f3f4f6',
                color: theme === 'dark' ? '#e5e7eb' : '#374151',
                fontSize: 12,
                cursor: 'pointer',
              }}
            >
              <option value="default">Organization Demo</option>
              <optgroup label="Code Intelligence">
                {sampleList.filter(s => s.key.startsWith('code')).map(s => (
                  <option key={s.key} value={s.key}>{s.name}</option>
                ))}
              </optgroup>
              <optgroup label="Social Network">
                {sampleList.filter(s => s.key.startsWith('social')).map(s => (
                  <option key={s.key} value={s.key}>{s.name}</option>
                ))}
              </optgroup>
              <optgroup label="Knowledge Graph">
                {sampleList.filter(s => s.key.startsWith('knowledge')).map(s => (
                  <option key={s.key} value={s.key}>{s.name}</option>
                ))}
              </optgroup>
              <optgroup label="Infrastructure">
                {sampleList.filter(s => s.key.startsWith('infra')).map(s => (
                  <option key={s.key} value={s.key}>{s.name}</option>
                ))}
              </optgroup>
            </select>
          </div>

          {/* Node/Edge count */}
          <span
            style={{
              fontSize: 11,
              color: theme === 'dark' ? '#9ca3af' : '#6b7280',
              padding: '4px 8px',
              background: theme === 'dark' ? '#374151' : '#f3f4f6',
              borderRadius: 4,
            }}
          >
            {data.nodes.length} nodes / {data.edges.length} edges
          </span>

          {/* Time bar toggle */}
          <label
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 8,
              fontSize: 13,
              color: theme === 'dark' ? '#e5e7eb' : '#374151',
              cursor: 'pointer',
            }}
          >
            <input
              type="checkbox"
              checked={showTimeBar}
              onChange={(e) => setShowTimeBar(e.target.checked)}
              style={{ cursor: 'pointer' }}
            />
            Time Bar
          </label>
        </div>
      </div>

      {/* Toolbar */}
      <Toolbar
        onLayoutChange={handleLayoutChange}
        onColorByMetric={handleColorByMetric}
        onColorByCommunity={handleColorByCommunity}
        onClearHighlight={handleClearHighlight}
        onFitView={handleFitView}
        onFindPath={handleFindPath}
        onScaleByConnections={handleScaleByConnections}
        onGroupByAttribute={handleGroupByAttribute}
        onUngroupAll={handleUngroupAll}
        onResetColors={handleResetColors}
        theme={theme}
        onThemeChange={setTheme}
        currentLayout={currentLayout}
        scaledByConnections={scaledByConnections}
        groupedBy={groupedBy}
      />

      {/* Chart area */}
      <div style={{ flex: 1, position: 'relative' }}>
        <Chart
          ref={chartRef}
          nodes={data.nodes}
          edges={data.edges}
          config={{
            layout: { type: currentLayout, direction: 'TB' },
            theme,
            fitView: true,
          }}
          onNodeClick={handleNodeClick}
          showTimeBar={showTimeBar}
          showMiniMap={true}
          showControls={true}
        />

        {/* Info panel */}
        <InfoPanel
          selectedNode={selectedNode}
          metrics={chartRef.current?.getMetrics() || null}
          theme={theme}
        />
      </div>
    </div>
  );
}
