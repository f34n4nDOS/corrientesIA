import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ChatService } from './chat.service';

interface Mensaje {
  autor: 'usuario' | 'bot';
  texto: string;
}

@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div>
      <div style="min-height:200px;border:1px solid #333;border-radius:8px;padding:12px;margin-bottom:12px;">
        <div *ngFor="let m of mensajes()" style="margin-bottom:8px;">
          <strong>{{ m.autor === 'usuario' ? 'Vos' : 'CorrientesIA' }}:</strong> {{ m.texto }}
        </div>
      </div>
      <input [(ngModel)]="entrada" (keyup.enter)="enviar()" placeholder="Pregunta algo sobre Corrientes..." style="width:70%;padding:8px;" />
      <button (click)="enviar()" style="padding:8px 16px;">Enviar</button>
    </div>
  `
})
export class ChatComponent {
  mensajes = signal<Mensaje[]>([]);
  entrada = '';

  constructor(private chatService: ChatService) {}

  enviar() {
    const texto = this.entrada.trim();
    if (!texto) return;
    this.mensajes.update(m => [...m, { autor: 'usuario', texto }]);
    this.entrada = '';

    this.chatService.enviarMensaje(texto).subscribe({
      next: res => this.mensajes.update(m => [...m, { autor: 'bot', texto: res.respuesta }]),
      error: () => this.mensajes.update(m => [...m, { autor: 'bot', texto: 'Error conectando con la API.' }])
    });
  }
}
