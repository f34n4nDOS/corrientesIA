import { Component } from '@angular/core';
import { ChatComponent } from './chat/chat.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [ChatComponent],
  template: `
    <div class="page">
      <header class="header">
        <h1>Corrientes<em>IA</em></h1>
        <p>Un modelo de lenguaje propio, entrenado desde cero sobre Corrientes, Argentina.</p>
      </header>

      <svg class="divider" viewBox="0 0 400 16" preserveAspectRatio="none" aria-hidden="true">
        <path d="M0 8 Q 25 0, 50 8 T 100 8 T 150 8 T 200 8 T 250 8 T 300 8 T 350 8 T 400 8"
              fill="none" stroke="currentColor" stroke-width="1.5" />
      </svg>

      <main class="content">
        <app-chat></app-chat>
      </main>
    </div>
  `,
  styles: [`
    .page {
      min-height: 100%;
      max-width: 680px;
      margin: 0 auto;
      padding: 48px 20px 32px;
      display: flex;
      flex-direction: column;
    }

    .header h1 {
      font-family: var(--font-display);
      font-weight: 600;
      font-size: 2.4rem;
      margin: 0 0 8px;
      letter-spacing: -0.01em;
    }

    .header h1 em {
      font-style: italic;
      font-weight: 500;
      color: var(--accent);
    }

    .header p {
      margin: 0;
      color: var(--text-muted);
      font-size: 0.98rem;
      max-width: 46ch;
      line-height: 1.5;
    }

    .divider {
      width: 100%;
      height: 14px;
      color: var(--border-strong);
      margin: 28px 0 24px;
    }

    .content {
      flex: 1;
      display: flex;
      min-height: 0;
    }
  `]
})
export class AppComponent {}
