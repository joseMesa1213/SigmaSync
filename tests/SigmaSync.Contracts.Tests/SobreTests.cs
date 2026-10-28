using System.Text.Json;

namespace SigmaSync.Contracts.Tests;

public class SobreTests
{
    private static FacturaOc FacturaEjemplo() => new(
        new FacturaEncabezado("*33570", "A", "2853060", DateTimeOffset.Parse("2026-10-19T14:09:38-05:00"),
            1301177.60m, 65058.88m, 0, 0, 1236118.72m, 0, 234862.56m, 1470981.28m),
        [new FacturaItem(1, "1I21033062400P4", 12, 19117.65m, 19117.647m, 229411.80m, false, null, null)],
        [new FacturaImpuesto("IVA", 19, 1236118.72m, 234862.557m)]);

    [Fact]
    public void Serializa_en_snake_case_con_los_nombres_del_contrato()
    {
        var sobre = Sobre.Crear(TiposMensaje.FacturaOc, "FAB", "PV_MAY", Sobre.Clave("FAB", "A", "*33570"), FacturaEjemplo());

        var json = sobre.ToJson();

        foreach (var campo in new[] { "\"msg_id\"", "\"nodo_origen\"", "\"clave_particion\"", "\"creado_ts\"", "\"t_gravable\"",
                     "\"mto_tax\"", "\"descto1\"", "\"precio_o\"", "\"total_item\"", "\"es_exento\"", "\"pedido_origen\"", "\"oc_origen\"", "\"cod_taxs\"" })
            Assert.Contains(campo, json);
        Assert.Contains("\"clave_particion\":\"FAB:A:*33570\"", json);
    }

    [Fact]
    public void Ida_y_vuelta_conserva_los_montos_sin_redondear()
    {
        var original = Sobre.Crear(TiposMensaje.FacturaOc, "FAB", "PV_MAY", "FAB:A:*33570", FacturaEjemplo());

        var leido = Sobre.FromJson(original.ToJson()).Leer<FacturaOc>();

        Assert.Equal(19117.647m, leido.Items[0].PrecioO);
        Assert.Equal(234862.557m, leido.Impuestos[0].Monto);
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T14:09:38-05:00"), leido.Factura.FechaE);
    }

    [Fact]
    public void Lee_el_ejemplo_del_contrato_v1_sin_los_campos_de_v1_1()
    {
        const string v1 = """
            { "version": "1.0", "msg_id": "6f1c2e8a-9b4d-4c1e-8a7f-2d3e4f5a6b7c", "tipo": "FACTURA_OC",
              "nodo_origen": "FAB", "nodo_destino": "PV_MAY", "creado_ts": "2026-10-25T10:15:00-05:00",
              "clave_particion": "FAB:A:*33570",
              "payload": {
                "factura": { "numero": "*33570", "tipo_fac": "A", "fecha_e": "2026-10-19T14:09:38-05:00",
                  "monto": 1301177.60, "descto1": 65058.88, "descto2": 0, "fletes": 0,
                  "t_gravable": 1236118.72, "t_exento": 0, "mto_tax": 234862.56, "mto_total": 1470981.28 },
                "items": [ { "linea": 1, "cod_item": "1I21033062400P4", "cantidad": 12, "precio": 19117.65, "precio_o": 19117.647,
                  "total_item": 229411.80, "es_exento": false, "pedido_origen": null } ],
                "impuestos": [ { "cod_taxs": "IVA", "tarifa": 19, "base": 1236118.72, "monto": 234862.557 } ] } }
            """;

        var sobre = Sobre.FromJson(v1);
        var factura = sobre.Leer<FacturaOc>();

        Assert.True(sobre.VersionCompatible);
        Assert.Equal(65058.88m, factura.Factura.Descto1);
        Assert.Null(factura.Items[0].OcOrigen);
    }

    [Fact]
    public void Fecha_de_la_OC_viaja_como_fecha_sin_hora()
    {
        var oc = new OcPedido(new OcEncabezado("000003538", "00000", "890919355-4", new DateOnly(2026, 9, 1),
            DateTimeOffset.Parse("2026-09-01T09:21:42-05:00"), 18047), [new OcItem(1, "1I21085125150SM", 250, 8965.1215m)]);

        var json = JsonSerializer.Serialize(oc, SigmaJson.Opciones);

        Assert.Contains("\"fecha_e\":\"2026-09-01\"", json);
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.1", true)]
    [InlineData("1.7", true)]
    [InlineData("2.0", false)]
    public void Acepta_solo_la_version_mayor_1(string version, bool compatible)
    {
        var sobre = Sobre.Crear(TiposMensaje.Ping, "A", "B", "A:PING:1", new Ping("x", DateTimeOffset.Now)) with { Version = version };

        Assert.Equal(compatible, sobre.VersionCompatible);
    }
}
