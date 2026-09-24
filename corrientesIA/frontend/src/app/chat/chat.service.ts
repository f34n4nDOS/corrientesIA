import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface ChatResponse {
  respuesta: string;
}

@Injectable({ providedIn: 'root' })
export class ChatService {
  private readonly apiUrl = `${environment.apiUrl}/chat`;

  constructor(private http: HttpClient) {}

  enviarMensaje(mensaje: string): Observable<ChatResponse> {
    return this.http.post<ChatResponse>(this.apiUrl, { mensaje });
  }
}
