// TimeBar Component for NVL Graph
// Provides temporal filtering of nodes and edges

import React, { useState, useMemo, useCallback, useRef, useEffect } from 'react';

export interface TimeBarProps {
  items: Array<{ id: string; timestamp?: number | Date }>;
  selectedRange?: { start: number; end: number };
  onChange: (range: { start: number; end: number }) => void;
  height?: number;
  showHistogram?: boolean;
  dateFormat?: (date: Date) => string;
  theme?: 'light' | 'dark';
}

interface HistogramBin {
  start: number;
  end: number;
  count: number;
}

const TimeBar: React.FC<TimeBarProps> = ({
  items,
  selectedRange,
  onChange,
  height = 100,
  showHistogram = true,
  dateFormat = (date) => date.toLocaleDateString(),
  theme = 'light',
}) => {
  const containerRef = useRef<HTMLDivElement>(null);
  const [isDragging, setIsDragging] = useState<'start' | 'end' | 'range' | null>(null);
  const [dragStartX, setDragStartX] = useState(0);
  const [dragStartRange, setDragStartRange] = useState({ start: 0, end: 0 });

  // Calculate time bounds
  const timeBounds = useMemo(() => {
    const timestamps = items
      .map((item) => {
        if (!item.timestamp) return null;
        return typeof item.timestamp === 'number' 
          ? item.timestamp 
          : item.timestamp.getTime();
      })
      .filter((t): t is number => t !== null);

    if (timestamps.length === 0) {
      const now = Date.now();
      return { min: now - 86400000, max: now };
    }

    return {
      min: Math.min(...timestamps),
      max: Math.max(...timestamps),
    };
  }, [items]);

  // Initialize range if not provided
  const range = useMemo(() => {
    return selectedRange || { start: timeBounds.min, end: timeBounds.max };
  }, [selectedRange, timeBounds]);

  // Calculate histogram bins
  const histogram = useMemo(() => {
    const binCount = 50;
    const binWidth = (timeBounds.max - timeBounds.min) / binCount;
    const bins: HistogramBin[] = [];

    for (let i = 0; i < binCount; i++) {
      bins.push({
        start: timeBounds.min + i * binWidth,
        end: timeBounds.min + (i + 1) * binWidth,
        count: 0,
      });
    }

    items.forEach((item) => {
      if (!item.timestamp) return;
      const ts = typeof item.timestamp === 'number' 
        ? item.timestamp 
        : item.timestamp.getTime();
      const binIndex = Math.min(
        Math.floor((ts - timeBounds.min) / binWidth),
        binCount - 1
      );
      if (binIndex >= 0) {
        bins[binIndex].count++;
      }
    });

    return bins;
  }, [items, timeBounds]);

  const maxCount = useMemo(() => {
    return Math.max(...histogram.map((b) => b.count), 1);
  }, [histogram]);

  // Convert timestamp to position
  const timeToPosition = useCallback((time: number): number => {
    const totalRange = timeBounds.max - timeBounds.min;
    if (totalRange === 0) return 0;
    return ((time - timeBounds.min) / totalRange) * 100;
  }, [timeBounds]);

  // Convert position to timestamp
  const positionToTime = useCallback((position: number): number => {
    const totalRange = timeBounds.max - timeBounds.min;
    return timeBounds.min + (position / 100) * totalRange;
  }, [timeBounds]);

  // Handle mouse events
  const handleMouseDown = useCallback((
    e: React.MouseEvent,
    type: 'start' | 'end' | 'range'
  ) => {
    e.preventDefault();
    setIsDragging(type);
    setDragStartX(e.clientX);
    setDragStartRange({ ...range });
  }, [range]);

  const handleMouseMove = useCallback((e: MouseEvent) => {
    if (!isDragging || !containerRef.current) return;

    const rect = containerRef.current.getBoundingClientRect();
    const deltaX = e.clientX - dragStartX;
    const deltaPercent = (deltaX / rect.width) * 100;
    const deltaTime = positionToTime(deltaPercent) - timeBounds.min;

    let newStart = dragStartRange.start;
    let newEnd = dragStartRange.end;

    if (isDragging === 'start') {
      newStart = Math.max(
        timeBounds.min,
        Math.min(dragStartRange.start + deltaTime, dragStartRange.end - 1000)
      );
    } else if (isDragging === 'end') {
      newEnd = Math.min(
        timeBounds.max,
        Math.max(dragStartRange.end + deltaTime, dragStartRange.start + 1000)
      );
    } else if (isDragging === 'range') {
      const rangeWidth = dragStartRange.end - dragStartRange.start;
      newStart = Math.max(
        timeBounds.min,
        Math.min(dragStartRange.start + deltaTime, timeBounds.max - rangeWidth)
      );
      newEnd = newStart + rangeWidth;
    }

    onChange({ start: newStart, end: newEnd });
  }, [isDragging, dragStartX, dragStartRange, positionToTime, timeBounds, onChange]);

  const handleMouseUp = useCallback(() => {
    setIsDragging(null);
  }, []);

  useEffect(() => {
    if (isDragging) {
      window.addEventListener('mousemove', handleMouseMove);
      window.addEventListener('mouseup', handleMouseUp);
      return () => {
        window.removeEventListener('mousemove', handleMouseMove);
        window.removeEventListener('mouseup', handleMouseUp);
      };
    }
  }, [isDragging, handleMouseMove, handleMouseUp]);

  // Theme styles
  const colors = useMemo(() => ({
    bg: theme === 'dark' ? '#1f2937' : '#ffffff',
    border: theme === 'dark' ? '#374151' : '#e5e7eb',
    text: theme === 'dark' ? '#e5e7eb' : '#374151',
    bar: theme === 'dark' ? '#4b5563' : '#d1d5db',
    barActive: theme === 'dark' ? '#6366f1' : '#6366f1',
    selection: theme === 'dark' ? 'rgba(99, 102, 241, 0.3)' : 'rgba(99, 102, 241, 0.2)',
    handle: '#6366f1',
  }), [theme]);

  const startPos = timeToPosition(range.start);
  const endPos = timeToPosition(range.end);

  return (
    <div
      ref={containerRef}
      style={{
        width: '100%',
        height,
        background: colors.bg,
        borderRadius: 8,
        border: `1px solid ${colors.border}`,
        padding: '8px 16px',
        display: 'flex',
        flexDirection: 'column',
        userSelect: 'none',
      }}
    >
      {/* Date labels */}
      <div
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          fontSize: 11,
          color: colors.text,
          marginBottom: 8,
        }}
      >
        <span>{dateFormat(new Date(range.start))}</span>
        <span style={{ fontWeight: 600 }}>
          {items.filter((item) => {
            if (!item.timestamp) return false;
            const ts = typeof item.timestamp === 'number' 
              ? item.timestamp 
              : item.timestamp.getTime();
            return ts >= range.start && ts <= range.end;
          }).length} items selected
        </span>
        <span>{dateFormat(new Date(range.end))}</span>
      </div>

      {/* Histogram and selection */}
      <div
        style={{
          flex: 1,
          position: 'relative',
          background: colors.border,
          borderRadius: 4,
          overflow: 'hidden',
        }}
      >
        {/* Histogram bars */}
        {showHistogram && (
          <div
            style={{
              position: 'absolute',
              inset: 0,
              display: 'flex',
              alignItems: 'flex-end',
              gap: 1,
              padding: '0 1px',
            }}
          >
            {histogram.map((bin, index) => {
              const isInRange = bin.start >= range.start && bin.end <= range.end;
              return (
                <div
                  key={index}
                  style={{
                    flex: 1,
                    height: `${(bin.count / maxCount) * 100}%`,
                    background: isInRange ? colors.barActive : colors.bar,
                    borderRadius: '2px 2px 0 0',
                    transition: 'background 0.2s',
                    minHeight: bin.count > 0 ? 2 : 0,
                  }}
                />
              );
            })}
          </div>
        )}

        {/* Selection overlay */}
        <div
          style={{
            position: 'absolute',
            left: `${startPos}%`,
            width: `${endPos - startPos}%`,
            top: 0,
            bottom: 0,
            background: colors.selection,
            cursor: 'move',
          }}
          onMouseDown={(e) => handleMouseDown(e, 'range')}
        />

        {/* Start handle */}
        <div
          style={{
            position: 'absolute',
            left: `${startPos}%`,
            top: 0,
            bottom: 0,
            width: 12,
            marginLeft: -6,
            cursor: 'ew-resize',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
          }}
          onMouseDown={(e) => handleMouseDown(e, 'start')}
        >
          <div
            style={{
              width: 4,
              height: '60%',
              background: colors.handle,
              borderRadius: 2,
              boxShadow: '0 1px 3px rgba(0, 0, 0, 0.2)',
            }}
          />
        </div>

        {/* End handle */}
        <div
          style={{
            position: 'absolute',
            left: `${endPos}%`,
            top: 0,
            bottom: 0,
            width: 12,
            marginLeft: -6,
            cursor: 'ew-resize',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
          }}
          onMouseDown={(e) => handleMouseDown(e, 'end')}
        >
          <div
            style={{
              width: 4,
              height: '60%',
              background: colors.handle,
              borderRadius: 2,
              boxShadow: '0 1px 3px rgba(0, 0, 0, 0.2)',
            }}
          />
        </div>
      </div>

      {/* Full range labels */}
      <div
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          fontSize: 10,
          color: colors.text,
          opacity: 0.6,
          marginTop: 4,
        }}
      >
        <span>{dateFormat(new Date(timeBounds.min))}</span>
        <span>{dateFormat(new Date(timeBounds.max))}</span>
      </div>
    </div>
  );
};

export default TimeBar;
