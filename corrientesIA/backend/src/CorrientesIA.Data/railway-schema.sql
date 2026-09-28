CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (
    `MigrationId` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
    `ProductVersion` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
    CONSTRAINT `PK___EFMigrationsHistory` PRIMARY KEY (`MigrationId`)
) CHARACTER SET=utf8mb4;

START TRANSACTION;

ALTER DATABASE CHARACTER SET utf8mb4;

CREATE TABLE `CorpusDocumentos` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Fuente` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Titulo` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Contenido` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Procesado` tinyint(1) NOT NULL,
    `FechaScrapeo` datetime(6) NOT NULL,
    CONSTRAINT `PK_CorpusDocumentos` PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;

CREATE TABLE `DatosDuros` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Clave` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `Valor` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Fuente` longtext CHARACTER SET utf8mb4 NOT NULL,
    `FechaActualizacion` datetime(6) NOT NULL,
    CONSTRAINT `PK_DatosDuros` PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;

CREATE TABLE `Lugares` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Nombre` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Categoria` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Descripcion` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Localidad` longtext CHARACTER SET utf8mb4 NULL,
    `Latitud` double NULL,
    `Longitud` double NULL,
    `FechaActualizacion` datetime(6) NOT NULL,
    CONSTRAINT `PK_Lugares` PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;

CREATE UNIQUE INDEX `IX_DatosDuros_Clave` ON `DatosDuros` (`Clave`);

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20260927212450_InitialCreate', '8.0.8');

COMMIT;

