import { useEffect } from 'react';
import type { Product } from '@/types';
import { productIconDataUrl } from '@/products/ProductIcon';

/**
 * Sets <link rel="icon"> to the active product's auto-generated icon, also
 * updates <title> to "{Product} · Forge". Falls back to the default Forge
 * favicon when no product is active.
 *
 * Idempotent: re-runs cheaply on every render, only mutates the DOM when the
 * resolved data URL actually changes.
 */
export function useProductFavicon(product: Product | null) {
  useEffect(() => {
    if (typeof document === 'undefined') return;

    const head = document.head;
    let link = head.querySelector<HTMLLinkElement>('link[rel="icon"][data-forge-product]');
    if (!link) {
      link = document.createElement('link');
      link.rel = 'icon';
      link.setAttribute('data-forge-product', '1');
      head.appendChild(link);
    }
    const href = product
      ? productIconDataUrl(product, 64)
      : '';
    if (href && link.href !== href) {
      link.type = 'image/svg+xml';
      link.href = href;
    } else if (!href && link.parentNode) {
      link.parentNode.removeChild(link);
    }

    if (product?.name) {
      const target = `${product.name} · Forge`;
      if (document.title !== target) document.title = target;
    }
  }, [product?.id, product?.name]);
}
