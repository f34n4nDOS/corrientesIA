CREATE DATABASE IF NOT EXISTS corrientesia CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE corrientesia;

CREATE TABLE IF NOT EXISTS Lugares (
    Id INT AUTO_INCREMENT PRIMARY KEY,
    Nombre VARCHAR(255) NOT NULL,
    Categoria VARCHAR(100) NOT NULL,
    Descripcion TEXT NOT NULL,
    Localidad VARCHAR(150),
    Latitud DOUBLE NULL,
    Longitud DOUBLE NULL,
    FechaActualizacion DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS DatosDuros (
    Id INT AUTO_INCREMENT PRIMARY KEY,
    Clave VARCHAR(150) NOT NULL UNIQUE,
    Valor VARCHAR(500) NOT NULL,
    Fuente VARCHAR(255) NOT NULL,
    FechaActualizacion DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS CorpusDocumentos (
    Id INT AUTO_INCREMENT PRIMARY KEY,
    Fuente VARCHAR(500) NOT NULL,
    Titulo VARCHAR(500) NOT NULL,
    Contenido LONGTEXT NOT NULL,
    Procesado BOOLEAN NOT NULL DEFAULT FALSE,
    FechaScrapeo DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Datos de ejemplo para arrancar a probar la API sin esperar al scraper
INSERT INTO DatosDuros (Clave, Valor, Fuente) VALUES
    ('poblacion_capital_2022', '358223', 'INDEC Censo 2022'),
    ('fundacion_ciudad_corrientes', '1588-04-03', 'Wikipedia');

INSERT INTO Lugares (Nombre, Categoria, Descripcion, Localidad) VALUES
    ('Costanera General San Martin', 'turismo', 'Paseo costero sobre el rio Parana, punto turistico central de la ciudad.', 'Corrientes Capital'),
    ('Esteros del Ibera', 'turismo', 'Reserva natural, uno de los humedales mas importantes del mundo.', 'Provincia de Corrientes');
