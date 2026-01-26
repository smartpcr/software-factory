// Sample Data Generators for ReGraph Clone
// Provides sample datasets for different domains and sizes

import type { ReGraphNode, ReGraphEdge } from '../types';

export interface SampleData {
  name: string;
  description: string;
  nodes: ReGraphNode[];
  edges: ReGraphEdge[];
}

// ============================================================================
// CODE INTELLIGENCE SAMPLES
// Simulates code structure: namespaces, classes, methods, and call relationships
// ============================================================================

function generateCodeIntelligence(size: 'small' | 'medium' | 'large'): SampleData {
  const nodes: ReGraphNode[] = [];
  const edges: ReGraphEdge[] = [];

  const config = {
    small: { namespaces: 2, classesPerNs: 2, methodsPerClass: 2 },
    medium: { namespaces: 4, classesPerNs: 5, methodsPerClass: 4 },
    large: { namespaces: 10, classesPerNs: 10, methodsPerClass: 10 },
  }[size];

  const colors = {
    namespace: '#8b5cf6',
    class: '#3b82f6',
    interface: '#06b6d4',
    method: '#10b981',
    property: '#f59e0b',
  };

  let nodeId = 0;
  const classNodes: string[] = [];
  const methodNodes: string[] = [];

  // Generate namespaces
  for (let ns = 0; ns < config.namespaces; ns++) {
    const nsId = `ns_${ns}`;
    nodes.push({
      id: nsId,
      label: `Namespace${ns}`,
      icon: '📦',
      color: colors.namespace,
      size: 70,
      data: { type: 'namespace', level: 0 },
    });

    // Generate classes per namespace
    for (let c = 0; c < config.classesPerNs; c++) {
      const classId = `class_${nodeId++}`;
      const isInterface = Math.random() > 0.8;

      nodes.push({
        id: classId,
        label: isInterface ? `I${randomClassName()}` : randomClassName(),
        icon: isInterface ? '📋' : '📄',
        color: isInterface ? colors.interface : colors.class,
        size: 55,
        data: { type: isInterface ? 'interface' : 'class', namespace: nsId, level: 1 },
      });

      edges.push({
        id: `e_${nsId}_${classId}`,
        source: nsId,
        target: classId,
        label: 'contains',
        color: '#94a3b8',
      });

      classNodes.push(classId);

      // Generate methods per class
      for (let m = 0; m < config.methodsPerClass; m++) {
        const methodId = `method_${nodeId++}`;
        const isPublic = Math.random() > 0.3;

        nodes.push({
          id: methodId,
          label: randomMethodName(),
          icon: isPublic ? '🔓' : '🔒',
          color: colors.method,
          size: 40,
          data: { type: 'method', class: classId, visibility: isPublic ? 'public' : 'private', level: 2 },
        });

        edges.push({
          id: `e_${classId}_${methodId}`,
          source: classId,
          target: methodId,
          color: '#94a3b8',
        });

        methodNodes.push(methodId);
      }
    }
  }

  // Add inheritance relationships between classes
  for (let i = 1; i < classNodes.length; i++) {
    if (Math.random() > 0.7) {
      const parentIdx = Math.floor(Math.random() * i);
      edges.push({
        id: `inherit_${i}`,
        source: classNodes[parentIdx],
        target: classNodes[i],
        label: 'extends',
        style: 'dashed',
        color: '#ec4899',
      });
    }
  }

  // Add method call relationships
  const callCount = Math.floor(methodNodes.length * 0.3);
  for (let i = 0; i < callCount; i++) {
    const from = methodNodes[Math.floor(Math.random() * methodNodes.length)];
    const to = methodNodes[Math.floor(Math.random() * methodNodes.length)];
    if (from !== to) {
      edges.push({
        id: `call_${i}`,
        source: from,
        target: to,
        label: 'calls',
        style: 'dotted',
        color: '#f97316',
        animated: true,
      });
    }
  }

  return {
    name: `Code Intelligence (${size})`,
    description: `Code structure with ${nodes.length} nodes and ${edges.length} edges`,
    nodes,
    edges,
  };
}

// ============================================================================
// SOCIAL NETWORK SAMPLES
// Simulates social connections: people with friendships and group memberships
// ============================================================================

function generateSocialNetwork(size: 'small' | 'medium' | 'large'): SampleData {
  const nodes: ReGraphNode[] = [];
  const edges: ReGraphEdge[] = [];

  const config = {
    small: { people: 15, groups: 3, connectionDensity: 0.2 },
    medium: { people: 80, groups: 8, connectionDensity: 0.08 },
    large: { people: 500, groups: 20, connectionDensity: 0.01 },
  }[size];

  const colors = ['#6366f1', '#ec4899', '#10b981', '#f59e0b', '#8b5cf6', '#06b6d4', '#ef4444', '#84cc16'];
  const icons = ['👤', '👩', '👨', '🧑', '👴', '👵', '🧔', '👱'];

  // Generate groups
  for (let g = 0; g < config.groups; g++) {
    nodes.push({
      id: `group_${g}`,
      label: randomGroupName(),
      icon: '👥',
      color: colors[g % colors.length],
      size: 70,
      data: { type: 'group', memberCount: 0 },
    });
  }

  // Generate people
  const peopleIds: string[] = [];
  for (let p = 0; p < config.people; p++) {
    const personId = `person_${p}`;
    const groupIdx = Math.floor(Math.random() * config.groups);

    nodes.push({
      id: personId,
      label: randomPersonName(),
      icon: icons[Math.floor(Math.random() * icons.length)],
      color: colors[groupIdx % colors.length],
      size: 40 + Math.random() * 20,
      data: {
        type: 'person',
        group: `group_${groupIdx}`,
        age: 20 + Math.floor(Math.random() * 50),
        active: Math.random() > 0.2,
      },
      timestamp: Date.now() - Math.random() * 30 * 24 * 60 * 60 * 1000,
    });

    // Connect to group
    edges.push({
      id: `member_${p}`,
      source: `group_${groupIdx}`,
      target: personId,
      color: '#94a3b8',
    });

    peopleIds.push(personId);
  }

  // Generate friendships
  for (let i = 0; i < peopleIds.length; i++) {
    for (let j = i + 1; j < peopleIds.length; j++) {
      if (Math.random() < config.connectionDensity) {
        const strength = Math.random();
        edges.push({
          id: `friend_${i}_${j}`,
          source: peopleIds[i],
          target: peopleIds[j],
          label: strength > 0.7 ? 'close friend' : undefined,
          width: 1 + strength * 3,
          color: strength > 0.7 ? '#ec4899' : '#94a3b8',
          data: { strength },
        });
      }
    }
  }

  return {
    name: `Social Network (${size})`,
    description: `Social graph with ${nodes.length} nodes and ${edges.length} edges`,
    nodes,
    edges,
  };
}

// ============================================================================
// KNOWLEDGE GRAPH SAMPLES
// Simulates a knowledge base: concepts, entities, and relationships
// ============================================================================

function generateKnowledgeGraph(size: 'small' | 'medium' | 'large'): SampleData {
  const nodes: ReGraphNode[] = [];
  const edges: ReGraphEdge[] = [];

  const config = {
    small: { concepts: 5, entitiesPerConcept: 3 },
    medium: { concepts: 15, entitiesPerConcept: 6 },
    large: { concepts: 50, entitiesPerConcept: 20 },
  }[size];

  const conceptTypes = [
    { name: 'Technology', icon: '💻', color: '#3b82f6' },
    { name: 'Science', icon: '🔬', color: '#10b981' },
    { name: 'Business', icon: '💼', color: '#f59e0b' },
    { name: 'Art', icon: '🎨', color: '#ec4899' },
    { name: 'Geography', icon: '🌍', color: '#8b5cf6' },
  ];

  const relationTypes = ['is_a', 'part_of', 'related_to', 'used_by', 'created_by', 'located_in'];

  let nodeId = 0;
  const entityNodes: { id: string; concept: number }[] = [];

  // Generate concepts
  for (let c = 0; c < config.concepts; c++) {
    const type = conceptTypes[c % conceptTypes.length];
    const conceptId = `concept_${c}`;

    nodes.push({
      id: conceptId,
      label: `${type.name} ${Math.floor(c / conceptTypes.length) + 1}`,
      icon: type.icon,
      color: type.color,
      size: 65,
      data: { type: 'concept', category: type.name },
    });

    // Generate entities per concept
    for (let e = 0; e < config.entitiesPerConcept; e++) {
      const entityId = `entity_${nodeId++}`;

      nodes.push({
        id: entityId,
        label: randomEntityName(type.name),
        icon: '📌',
        color: type.color,
        size: 40,
        data: { type: 'entity', concept: conceptId, category: type.name },
      });

      edges.push({
        id: `belongs_${entityId}`,
        source: conceptId,
        target: entityId,
        label: 'contains',
        color: '#94a3b8',
      });

      entityNodes.push({ id: entityId, concept: c });
    }
  }

  // Add cross-concept relationships
  const crossLinks = Math.floor(entityNodes.length * 0.15);
  for (let i = 0; i < crossLinks; i++) {
    const from = entityNodes[Math.floor(Math.random() * entityNodes.length)];
    const to = entityNodes[Math.floor(Math.random() * entityNodes.length)];

    if (from.id !== to.id && from.concept !== to.concept) {
      const relType = relationTypes[Math.floor(Math.random() * relationTypes.length)];
      edges.push({
        id: `rel_${i}`,
        source: from.id,
        target: to.id,
        label: relType,
        style: 'dashed',
        color: '#06b6d4',
      });
    }
  }

  return {
    name: `Knowledge Graph (${size})`,
    description: `Knowledge base with ${nodes.length} nodes and ${edges.length} edges`,
    nodes,
    edges,
  };
}

// ============================================================================
// INFRASTRUCTURE / MICROSERVICES SAMPLES
// Simulates cloud infrastructure: services, databases, APIs
// ============================================================================

function generateInfrastructure(size: 'small' | 'medium' | 'large'): SampleData {
  const nodes: ReGraphNode[] = [];
  const edges: ReGraphEdge[] = [];

  const config = {
    small: { services: 5, databases: 2, queues: 1, gateways: 1 },
    medium: { services: 20, databases: 5, queues: 3, gateways: 2 },
    large: { services: 100, databases: 20, queues: 15, gateways: 5 },
  }[size];

  const serviceNames = ['auth', 'user', 'payment', 'order', 'inventory', 'notification', 'analytics', 'search', 'recommendation', 'shipping'];

  // Add gateways
  for (let g = 0; g < config.gateways; g++) {
    nodes.push({
      id: `gateway_${g}`,
      label: `API Gateway ${g + 1}`,
      icon: '🚪',
      color: '#6366f1',
      size: 70,
      data: { type: 'gateway', traffic: Math.floor(Math.random() * 10000) },
    });
  }

  // Add services
  const serviceIds: string[] = [];
  for (let s = 0; s < config.services; s++) {
    const serviceId = `service_${s}`;
    const baseName = serviceNames[s % serviceNames.length];
    const status = Math.random() > 0.1 ? 'healthy' : 'warning';

    nodes.push({
      id: serviceId,
      label: `${baseName}-svc-${Math.floor(s / serviceNames.length)}`,
      icon: status === 'healthy' ? '🟢' : '🟡',
      color: status === 'healthy' ? '#10b981' : '#f59e0b',
      size: 50,
      data: {
        type: 'service',
        status,
        cpu: Math.floor(Math.random() * 100),
        memory: Math.floor(Math.random() * 100),
        replicas: 1 + Math.floor(Math.random() * 5),
      },
      glyphs: status === 'warning' ? [{ position: 'topRight', color: '#ef4444', text: '!' }] : undefined,
    });

    // Connect to gateway
    const gatewayIdx = Math.floor(Math.random() * config.gateways);
    edges.push({
      id: `gw_${s}`,
      source: `gateway_${gatewayIdx}`,
      target: serviceId,
      color: '#94a3b8',
    });

    serviceIds.push(serviceId);
  }

  // Add databases
  const dbIds: string[] = [];
  for (let d = 0; d < config.databases; d++) {
    const dbId = `db_${d}`;
    const dbType = ['PostgreSQL', 'MongoDB', 'Redis', 'MySQL', 'Elasticsearch'][d % 5];

    nodes.push({
      id: dbId,
      label: `${dbType}-${d}`,
      icon: '🗄️',
      color: '#8b5cf6',
      size: 55,
      data: { type: 'database', engine: dbType, size: `${Math.floor(Math.random() * 500)}GB` },
    });

    dbIds.push(dbId);
  }

  // Add message queues
  const queueIds: string[] = [];
  for (let q = 0; q < config.queues; q++) {
    const queueId = `queue_${q}`;

    nodes.push({
      id: queueId,
      label: `Queue-${q}`,
      icon: '📨',
      color: '#06b6d4',
      size: 50,
      data: { type: 'queue', pending: Math.floor(Math.random() * 1000) },
    });

    queueIds.push(queueId);
  }

  // Connect services to databases
  serviceIds.forEach((svcId, idx) => {
    const dbIdx = idx % config.databases;
    edges.push({
      id: `svc_db_${idx}`,
      source: svcId,
      target: dbIds[dbIdx],
      label: 'reads/writes',
      style: 'dashed',
      color: '#8b5cf6',
    });
  });

  // Connect services via queues
  for (let i = 0; i < serviceIds.length * 0.3; i++) {
    const from = serviceIds[Math.floor(Math.random() * serviceIds.length)];
    const to = serviceIds[Math.floor(Math.random() * serviceIds.length)];
    const queue = queueIds[Math.floor(Math.random() * queueIds.length)];

    if (from !== to) {
      edges.push({
        id: `pub_${i}`,
        source: from,
        target: queue,
        label: 'publishes',
        animated: true,
        color: '#06b6d4',
      });
      edges.push({
        id: `sub_${i}`,
        source: queue,
        target: to,
        label: 'subscribes',
        animated: true,
        color: '#06b6d4',
      });
    }
  }

  // Direct service-to-service calls
  for (let i = 0; i < serviceIds.length * 0.2; i++) {
    const from = serviceIds[Math.floor(Math.random() * serviceIds.length)];
    const to = serviceIds[Math.floor(Math.random() * serviceIds.length)];

    if (from !== to) {
      edges.push({
        id: `call_${i}`,
        source: from,
        target: to,
        label: 'HTTP',
        color: '#94a3b8',
      });
    }
  }

  return {
    name: `Infrastructure (${size})`,
    description: `Microservices with ${nodes.length} nodes and ${edges.length} edges`,
    nodes,
    edges,
  };
}

// ============================================================================
// HELPER FUNCTIONS
// ============================================================================

function randomClassName(): string {
  const prefixes = ['User', 'Order', 'Product', 'Payment', 'Auth', 'Data', 'Cache', 'Event', 'Message', 'Config'];
  const suffixes = ['Service', 'Controller', 'Repository', 'Manager', 'Handler', 'Factory', 'Builder', 'Validator'];
  return prefixes[Math.floor(Math.random() * prefixes.length)] + suffixes[Math.floor(Math.random() * suffixes.length)];
}

function randomMethodName(): string {
  const verbs = ['get', 'set', 'create', 'update', 'delete', 'find', 'validate', 'process', 'handle', 'transform'];
  const nouns = ['User', 'Data', 'Item', 'Record', 'Config', 'State', 'Result', 'Request', 'Response'];
  return verbs[Math.floor(Math.random() * verbs.length)] + nouns[Math.floor(Math.random() * nouns.length)];
}

function randomPersonName(): string {
  const firstNames = ['Alice', 'Bob', 'Carol', 'David', 'Eve', 'Frank', 'Grace', 'Henry', 'Ivy', 'Jack'];
  const lastNames = ['Smith', 'Johnson', 'Williams', 'Brown', 'Jones', 'Garcia', 'Miller', 'Davis', 'Wilson', 'Lee'];
  return `${firstNames[Math.floor(Math.random() * firstNames.length)]} ${lastNames[Math.floor(Math.random() * lastNames.length)]}`;
}

function randomGroupName(): string {
  const adjectives = ['Tech', 'Creative', 'Global', 'Digital', 'Smart', 'Modern', 'Future', 'Elite'];
  const nouns = ['Innovators', 'Creators', 'Builders', 'Pioneers', 'Leaders', 'Experts', 'Network', 'Community'];
  return `${adjectives[Math.floor(Math.random() * adjectives.length)]} ${nouns[Math.floor(Math.random() * nouns.length)]}`;
}

function randomEntityName(category: string): string {
  const names: Record<string, string[]> = {
    Technology: ['React', 'Node.js', 'Python', 'Docker', 'Kubernetes', 'AWS', 'GraphQL', 'TypeScript'],
    Science: ['Quantum Physics', 'Genetics', 'AI', 'Climate', 'Neuroscience', 'Chemistry', 'Biology'],
    Business: ['Strategy', 'Marketing', 'Finance', 'Operations', 'Sales', 'HR', 'Product'],
    Art: ['Impressionism', 'Cubism', 'Digital Art', 'Photography', 'Sculpture', 'Music'],
    Geography: ['Europe', 'Asia', 'Americas', 'Africa', 'Oceania', 'Arctic', 'Mountains'],
  };
  const items = names[category] || names.Technology;
  return items[Math.floor(Math.random() * items.length)] + ` ${Math.floor(Math.random() * 100)}`;
}

// ============================================================================
// EXPORT ALL SAMPLES
// ============================================================================

export const samples = {
  // Code Intelligence
  codeSmall: () => generateCodeIntelligence('small'),
  codeMedium: () => generateCodeIntelligence('medium'),
  codeLarge: () => generateCodeIntelligence('large'),

  // Social Network
  socialSmall: () => generateSocialNetwork('small'),
  socialMedium: () => generateSocialNetwork('medium'),
  socialLarge: () => generateSocialNetwork('large'),

  // Knowledge Graph
  knowledgeSmall: () => generateKnowledgeGraph('small'),
  knowledgeMedium: () => generateKnowledgeGraph('medium'),
  knowledgeLarge: () => generateKnowledgeGraph('large'),

  // Infrastructure
  infraSmall: () => generateInfrastructure('small'),
  infraMedium: () => generateInfrastructure('medium'),
  infraLarge: () => generateInfrastructure('large'),
};

export type SampleKey = keyof typeof samples;

export function getSampleList(): Array<{ key: SampleKey; name: string; description: string }> {
  return [
    { key: 'codeSmall', name: 'Code Intelligence (Small)', description: '~20 nodes - Classes and methods' },
    { key: 'codeMedium', name: 'Code Intelligence (Medium)', description: '~100 nodes - Multiple namespaces' },
    { key: 'codeLarge', name: 'Code Intelligence (Large)', description: '~1000 nodes - Large codebase' },
    { key: 'socialSmall', name: 'Social Network (Small)', description: '~20 nodes - Small community' },
    { key: 'socialMedium', name: 'Social Network (Medium)', description: '~100 nodes - Organization' },
    { key: 'socialLarge', name: 'Social Network (Large)', description: '~500 nodes - Large network' },
    { key: 'knowledgeSmall', name: 'Knowledge Graph (Small)', description: '~20 nodes - Basic ontology' },
    { key: 'knowledgeMedium', name: 'Knowledge Graph (Medium)', description: '~100 nodes - Domain knowledge' },
    { key: 'knowledgeLarge', name: 'Knowledge Graph (Large)', description: '~1000 nodes - Enterprise KB' },
    { key: 'infraSmall', name: 'Infrastructure (Small)', description: '~10 nodes - Simple architecture' },
    { key: 'infraMedium', name: 'Infrastructure (Medium)', description: '~30 nodes - Microservices' },
    { key: 'infraLarge', name: 'Infrastructure (Large)', description: '~150 nodes - Enterprise infra' },
  ];
}

export function loadSample(key: SampleKey): SampleData {
  return samples[key]();
}
