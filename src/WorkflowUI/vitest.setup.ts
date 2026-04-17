import '@testing-library/jest-dom';

// Mock ResizeObserver for React Flow in jsdom
if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}

// Mock DOMMatrixReadOnly for React Flow in jsdom
if (typeof globalThis.DOMMatrixReadOnly === 'undefined') {
  class MockDOMMatrixReadOnly {
    a = 1; b = 0; c = 0; d = 1; e = 0; f = 0;
    m11 = 1; m12 = 0; m13 = 0; m14 = 0;
    m21 = 0; m22 = 1; m23 = 0; m24 = 0;
    m31 = 0; m32 = 0; m33 = 1; m34 = 0;
    m41 = 0; m42 = 0; m43 = 0; m44 = 1;
    is2D = true;
    isIdentity = true;

    inverse() { return new MockDOMMatrixReadOnly(); }
    flipX() { return new MockDOMMatrixReadOnly(); }
    flipY() { return new MockDOMMatrixReadOnly(); }
    multiply() { return new MockDOMMatrixReadOnly(); }
    rotate() { return new MockDOMMatrixReadOnly(); }
    rotateAxisAngle() { return new MockDOMMatrixReadOnly(); }
    rotateFromVector() { return new MockDOMMatrixReadOnly(); }
    scale() { return new MockDOMMatrixReadOnly(); }
    scaleNonUniform() { return new MockDOMMatrixReadOnly(); }
    skewX() { return new MockDOMMatrixReadOnly(); }
    skewY() { return new MockDOMMatrixReadOnly(); }
    toFloat32Array() { return new Float32Array(16); }
    toFloat64Array() { return new Float64Array(16); }
    toJSON() { return {}; }
    transformPoint() { return { x: 0, y: 0, z: 0, w: 1 }; }
    translate() { return new MockDOMMatrixReadOnly(); }
  }
  globalThis.DOMMatrixReadOnly = MockDOMMatrixReadOnly as unknown as typeof DOMMatrixReadOnly;
}
