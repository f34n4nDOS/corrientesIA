import { Component, ElementRef, ViewChild, AfterViewChecked, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ChatService } from './chat.service';

interface Mensaje {
  autor: 'usuario' | 'bot';
  texto: string;
  datoVerificado?: string;
  etiquetaVerificado?: string;
  hora: string;
  esError?: boolean;
}

const SUGERENCIAS = [
  '¿Qué son los Esteros del Iberá?',
  '¿Qué es el chamamé?',
  '¿Dónde queda Goya?'
];

// Reconoce cualquier emoji al inicio seguido de una frase terminada en ":"
// (el backend usa 📍 para grounding por palabra clave, 📚 para grounding
// por corpus, y puede sumar más categorías en el futuro).
const PATRON_GROUNDING = /\p{Extended_Pictographic}\s*([^:]+):\s*/u;

@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="chat">
      <div class="historial" #historial>
        @if (mensajes().length === 0) {
          <div class="vacio">
            <p>Preguntame algo sobre la provincia — historia, turismo, cultura o datos concretos.</p>
            <div class="chips">
              @for (s of sugerencias; track s) {
                <button type="button" class="chip" (click)="enviarSugerencia(s)">{{ s }}</button>
              }
            </div>
          </div>
        }

        @for (m of mensajes(); track $index) {
          <div class="fila" [class.usuario]="m.autor === 'usuario'">
            <div class="burbuja" [class.usuario]="m.autor === 'usuario'" [class.error]="m.esError">
              @if (m.texto) {
                <p class="texto">{{ m.texto }}</p>
              }
              @if (m.datoVerificado) {
                <div class="verificado" [class.solo]="!m.texto">
                  <span class="marca">{{ m.etiquetaVerificado || 'Dato verificado' }}</span>
                  {{ m.datoVerificado }}
                </div>
              }
            </div>
            <span class="hora">{{ m.hora }}</span>
          </div>
        }

        @if (cargando()) {
          <div class="fila">
            <div class="burbuja escribiendo">
              <span></span><span></span><span></span>
            </div>
          </div>
        }
      </div>

      <form class="entrada" (ngSubmit)="enviar()">
        <input
          [(ngModel)]="texto"
          name="mensaje"
          placeholder="Pregunta algo sobre Corrientes..."
          [disabled]="cargando()"
          autocomplete="off"
        />
        <button type="submit" [disabled]="cargando() || !texto.trim()">
          Enviar
        </button>
      </form>
    </div>
  `,
  styles: [`
    .chat {
      display: flex;
      flex-direction: column;
      flex: 1;
      min-height: 0;
      width: 100%;
    }

    .historial {
      flex: 1;
      min-height: 320px;
      max-height: 60vh;
      overflow-y: auto;
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
      background: var(--bg-panel);
      padding: 20px;
      display: flex;
      flex-direction: column;
      gap: 14px;
    }

    .vacio {
      margin: auto 0;
      text-align: center;
      color: var(--text-muted);
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 18px;
    }

    .vacio p {
      margin: 0;
      max-width: 34ch;
      line-height: 1.5;
    }

    .chips {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      justify-content: center;
    }

    .chip {
      font-family: var(--font-body);
      font-size: 0.85rem;
      color: var(--text);
      background: var(--bg-elevated);
      border: 1px solid var(--border);
      border-radius: 999px;
      padding: 8px 14px;
      cursor: pointer;
      transition: border-color 0.15s ease;
    }

    .chip:hover {
      border-color: var(--accent);
    }

    .fila {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      max-width: 84%;
    }

    .fila.usuario {
      align-self: flex-end;
      align-items: flex-end;
    }

    .burbuja {
      background: var(--bot-bubble);
      border: 1px solid var(--border);
      border-radius: var(--radius-md);
      border-top-left-radius: 4px;
      padding: 12px 16px;
    }

    .burbuja.usuario {
      background: var(--user-bubble);
      border-top-left-radius: var(--radius-md);
      border-top-right-radius: 4px;
    }

    .burbuja.error {
      border-color: var(--error);
    }

    .texto {
      margin: 0;
      white-space: pre-wrap;
      line-height: 1.5;
      font-size: 0.96rem;
    }

    .verificado {
      margin-top: 10px;
      padding-top: 10px;
      border-top: 1px dashed var(--border-strong);
      font-size: 0.88rem;
      color: var(--verified);
      line-height: 1.5;
    }

    .verificado.solo {
      margin-top: 0;
      padding-top: 0;
      border-top: none;
    }

    .verificado .marca {
      display: block;
      font-family: var(--font-display);
      font-style: italic;
      font-weight: 500;
      color: var(--accent);
      margin-bottom: 2px;
    }

    .hora {
      font-size: 0.72rem;
      color: var(--text-faint);
      margin-top: 4px;
      padding: 0 4px;
    }

    .escribiendo {
      display: flex;
      gap: 4px;
      padding: 16px;
    }

    .escribiendo span {
      width: 6px;
      height: 6px;
      border-radius: 50%;
      background: var(--text-faint);
      animation: pulso 1.2s infinite ease-in-out;
    }

    .escribiendo span:nth-child(2) { animation-delay: 0.15s; }
    .escribiendo span:nth-child(3) { animation-delay: 0.3s; }

    @keyframes pulso {
      0%, 60%, 100% { opacity: 0.3; transform: translateY(0); }
      30% { opacity: 1; transform: translateY(-2px); }
    }

    .entrada {
      display: flex;
      gap: 10px;
      margin-top: 14px;
    }

    .entrada input {
      flex: 1;
      background: var(--bg-elevated);
      border: 1px solid var(--border);
      border-radius: var(--radius-sm);
      padding: 12px 16px;
      color: var(--text);
      font-family: var(--font-body);
      font-size: 0.95rem;
    }

    .entrada input::placeholder {
      color: var(--text-faint);
    }

    .entrada input:disabled {
      opacity: 0.6;
    }

    .entrada button {
      background: var(--accent);
      color: #16302b;
      border: none;
      border-radius: var(--radius-sm);
      padding: 0 22px;
      font-family: var(--font-body);
      font-weight: 600;
      font-size: 0.92rem;
      cursor: pointer;
      transition: background 0.15s ease;
    }

    .entrada button:hover:not(:disabled) {
      background: var(--accent-strong);
    }

    .entrada button:disabled {
      opacity: 0.5;
      cursor: not-allowed;
    }
  `]
})
export class ChatComponent implements AfterViewChecked {
  @ViewChild('historial') private historialEl?: ElementRef<HTMLDivElement>;

  mensajes = signal<Mensaje[]>([]);
  cargando = signal(false);
  texto = '';
  sugerencias = SUGERENCIAS;

  private debeScrollear = false;

  constructor(private chatService: ChatService) {}

  ngAfterViewChecked() {
    if (this.debeScrollear && this.historialEl) {
      this.historialEl.nativeElement.scrollTop = this.historialEl.nativeElement.scrollHeight;
      this.debeScrollear = false;
    }
  }

  enviarSugerencia(texto: string) {
    this.texto = texto;
    this.enviar();
  }

  enviar() {
    const texto = this.texto.trim();
    if (!texto || this.cargando()) return;

    this.agregarMensaje({ autor: 'usuario', texto, hora: this.horaActual() });
    this.texto = '';
    this.cargando.set(true);

    this.chatService.enviarMensaje(texto).subscribe({
      next: res => {
        const { textoBase, datoVerificado, etiquetaVerificado } = this.separarGrounding(res.respuesta);
        this.agregarMensaje({ autor: 'bot', texto: textoBase, datoVerificado, etiquetaVerificado, hora: this.horaActual() });
        this.cargando.set(false);
      },
      error: () => {
        this.agregarMensaje({
          autor: 'bot',
          texto: 'No pude conectar con la API. Verificá que el backend esté corriendo.',
          hora: this.horaActual(),
          esError: true
        });
        this.cargando.set(false);
      }
    });
  }

  private separarGrounding(respuesta: string): { textoBase: string; datoVerificado?: string; etiquetaVerificado?: string } {
    const match = respuesta.match(PATRON_GROUNDING);
    if (!match || match.index === undefined) return { textoBase: respuesta.trim() };

    return {
      textoBase: respuesta.slice(0, match.index).trim(),
      etiquetaVerificado: match[1].trim(),
      datoVerificado: respuesta.slice(match.index + match[0].length).trim()
    };
  }

  private agregarMensaje(m: Mensaje) {
    this.mensajes.update(actual => [...actual, m]);
    this.debeScrollear = true;
  }

  private horaActual(): string {
    return new Date().toLocaleTimeString('es-AR', { hour: '2-digit', minute: '2-digit' });
  }
}
