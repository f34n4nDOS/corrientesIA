import { Component } from '@angular/core';
import { ChatComponent } from './chat/chat.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [ChatComponent],
  template: `
    <main style="max-width:640px;margin:0 auto;padding:24px;">
      <h1>CorrientesIA</h1>
      <p>Chat sobre Corrientes, Argentina — modelo propio, sin APIs externas.</p>
      <app-chat></app-chat>
    </main>
  `
})
export class AppComponent {}
