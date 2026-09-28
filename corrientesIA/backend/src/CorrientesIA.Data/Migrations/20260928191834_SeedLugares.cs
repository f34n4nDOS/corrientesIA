using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CorrientesIA.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeedLugares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Lugares",
                columns: new[] { "Id", "Categoria", "Descripcion", "FechaActualizacion", "Latitud", "Localidad", "Longitud", "Nombre" },
                values: new object[,]
                {
                    { 1, "turismo", "Costanera de la ciudad de Corrientes.", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), null, "Corrientes Capital", null, "Costanera General San Martin" },
                    { 2, "turismo", "Gran humedal ubicado en la provincia de Corrientes.", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), null, "Provincia de Corrientes", null, "Esteros del Ibera" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Lugares",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Lugares",
                keyColumn: "Id",
                keyValue: 2);
        }
    }
}
